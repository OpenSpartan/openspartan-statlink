namespace OpenSpartan.StatLink.Models
{
    internal class Asset
    {
        public Asset()
        {
            Versions = new List<AssetVersion>();
        }
        public string Id { get; set; }
        public AssetClass Class { get; set; }
        public List<AssetVersion> Versions { get; set; }
    }
}
