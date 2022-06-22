namespace OpenSpartan.StatLink.Models
{
    internal class AssetVersion
    {
        public AssetVersion()
        {
            StatRecords = new List<Stat>();
        }

        public AssetMetadata Metadata { get; set; }
        public List<Stat> StatRecords { get; set; }
    }
}
