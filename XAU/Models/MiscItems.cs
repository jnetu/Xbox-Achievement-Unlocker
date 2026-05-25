
    public class GameItem
    {
        public string Title { get; set; }
        public string TitleId { get; set; }
        public bool IsTitleBased { get; set; }
    }

    public partial class MultiSpoofGameItem : ObservableObject
    {
        public string TitleId { get; set; }
        public string Name { get; set; }
        public string ImageUrl { get; set; }
        public string Gamerscore { get; set; }
        public string TimePlayed { get; set; }

        [ObservableProperty] private string _spoofingDuration = "00:00:00";
    }
