using XAU.ViewModels.Pages;

// Single entry point every spoofing loop (single, multi and auto spoofer) uses to talk to the
// presence API.
//
// It keeps the presence token fresh before each send and, when the API rejects the token, renews it
// and retries once. Long sessions used to die after ~8-10h because the token captured at start-up
// eventually expires: from then on every call was rejected, the loop broke out, and the only fix was
// pressing Start again. Nothing here throws -- failures come back as SpoofResult so the loops can
// simply retry on the next cycle.
public static class SpoofSender
{
    public static Task<SpoofResult> SendAsync(XboxRestAPI api, string xuid, string titleId) =>
        SendCoreAsync(() => api.SendSpoofAsync(xuid, titleId));

    public static Task<SpoofResult> SendAsync(XboxRestAPI api, string xuid, IReadOnlyList<string> titleIds) =>
        SendCoreAsync(() => api.SendSpoofAsync(xuid, titleIds));

    private static async Task<SpoofResult> SendCoreAsync(Func<Task<SpoofResult>> send)
    {
        try
        {
            await HomeViewModel.EnsureSpoofTokenFreshAsync();

            var result = await send();
            if (result.Success || !result.AuthRejected)
                return result;

            // Rejected token: renew it now (Xbox app rescan / new presence-scoped XSTS) and retry.
            if (!await HomeViewModel.EnsureSpoofTokenFreshAsync(force: true))
                return result;

            return await send();
        }
        catch (Exception ex)
        {
            return SpoofResult.Fail($"Spoof request failed: {ex.Message}");
        }
    }
}
