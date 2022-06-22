namespace OpenSpartan.StatLink.Models
{
    public class Stat
    {
        public DateTime SnapshotTime { get; set; }
        public long RecentPlays { get; set; }
        public long AllTimePlays { get; set; } 
        public long Favorites { get; set; }
        public long Likes { get; set; }
        public long Bookmarks { get; set; }
        public float AverageRating { get; set; }
        public long NumberOfRatings { get; set; }
    }
}
