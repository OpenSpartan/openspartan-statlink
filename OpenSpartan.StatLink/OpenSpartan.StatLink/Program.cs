using Grunt.Authentication;
using Grunt.Core;
using Grunt.Models;
using Grunt.Models.HaloInfinite;
using Microsoft.VisualBasic.FileIO;
using OpenSpartan.StatLink.Models;
using Sodium;
using System.CommandLine;
using System.Text.Encodings.Web;
using System.Text.Json;

class Program
{
    static XboxAuthenticationManager manager = new();
    static HaloAuthenticationClient haloAuthClient = new();
    static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand();

        var codeOption = new Option<string>(
            name: "--code",
            description: "Initial authorization code to perform the token exchange.",
            getDefaultValue: () => String.Empty);

        var clientIdOption = new Option<string>(
            name: "--client-id",
            description: "Registered client ID.",
            getDefaultValue: () => String.Empty);

        var clientSecretOption = new Option<string>(
            name: "--client-secret",
            description: "Registered client secret.",
            getDefaultValue: () => String.Empty);

        var redirectUrlOption = new Option<string>(
            name: "--redirect-url",
            description: "Redirect URL for the registered client.",
            getDefaultValue: () => String.Empty);

        var authTokenOption = new Option<string>(
            name: "--auth-token",
            description: "Authentication token to get the data.",
            getDefaultValue: () => String.Empty);

        var refreshTokenOption = new Option<string>(
            name: "--refresh-token",
            description: "Refresh token used to obtain a new token.",
            getDefaultValue: () => String.Empty);

        var outFolderOption = new Option<string>(
            name: "--out",
            description: "Output folder used for final results.",
            getDefaultValue: () => String.Empty);

        var buildIdOption = new Option<string>(
            name: "--build-id",
            description: "Build for which data is being referenced.",
            getDefaultValue: () => String.Empty);

        var projectIdOption = new Option<string>(
            name: "--project-id",
            description: "Unique identifier of the project for which stats need to be obtained.",
            getDefaultValue: () => String.Empty);

        var secretOption = new Option<string>(
            name: "--secret",
            description: "Value for the secret.",
            getDefaultValue: () => String.Empty);

        var publicKeyOption = new Option<string>(
            name: "--public-key",
            description: "Value for the public key used to encrypt the secret.",
            getDefaultValue: () => String.Empty);

        var startCommand = new Command("start", "Authenticate the user with the Xbox and Halo services.")
        {
            codeOption,
            clientIdOption,
            clientSecretOption,
            redirectUrlOption
        };

        var getUrlCommand = new Command("geturl", "Get the authentication URL which the user should go to for auth code production.")
        {
            clientIdOption,
            redirectUrlOption
        };

        var refreshCommand = new Command("refresh", "Refreshes the currently assigned token to a new one.")
        {
            clientIdOption,
            clientSecretOption,
            redirectUrlOption,
            refreshTokenOption
        };

        var getStatsCommand = new Command("getstats", "Get stats about currently available maps and game modes.")
        {
            authTokenOption,
            buildIdOption,
            projectIdOption,
            outFolderOption
        };

        var prepareSecret = new Command("prepsecret", "Prepares a secret to be updated in GitHub Actions.")
        {
            secretOption,
            publicKeyOption
        };

        rootCommand.AddCommand(startCommand);
        rootCommand.AddCommand(getUrlCommand);
        rootCommand.AddCommand(refreshCommand);
        rootCommand.AddCommand(getStatsCommand);
        rootCommand.AddCommand(prepareSecret);

        startCommand.SetHandler(async (code, clientId, clientSecret, redirectUrl) =>
        {
            var authResultString = await PerformAuthentication(code, clientId, clientSecret, redirectUrl);
            if (!string.IsNullOrEmpty(authResultString))
            {
                Console.WriteLine(authResultString);
            }
        }, codeOption, clientIdOption, clientSecretOption, redirectUrlOption);

        getUrlCommand.SetHandler((clientId, redirectUrl) =>
        {
            var url = manager.GenerateAuthUrl(clientId, redirectUrl);
            Console.WriteLine("You should be requesting the code from the following URL, if you don't have it yet:");
            Console.WriteLine(url);
        }, clientIdOption, redirectUrlOption);

        refreshCommand.SetHandler(async (clientId, clientSecret, redirectUrl, refreshToken) =>
        {
            var authResultString = await PerformTokenRefresh(refreshToken, clientId, clientSecret, redirectUrl);
            if (!string.IsNullOrEmpty(authResultString))
            {
                Console.WriteLine(authResultString);
            }
        }, clientIdOption, clientSecretOption, redirectUrlOption, refreshTokenOption);

        getStatsCommand.SetHandler(async (token, buildId, projectId, outFolder) =>
        {
            var acquisitionResult = await PerformStatsAcquisition(token, buildId, projectId, outFolder);
            if (acquisitionResult)
            {
                Console.WriteLine($"Wrote the stats to {outFolder}");
            }
            else
            {
                Console.WriteLine($"Could not write asset stats to {outFolder}.");
            }
        }, authTokenOption, buildIdOption, projectIdOption, outFolderOption);

        prepareSecret.SetHandler((secret, publicKey) =>
        {
            byte[] secretValue = System.Text.Encoding.UTF8.GetBytes(secret);
            byte[] publicKeyValue = Convert.FromBase64String(publicKey);

            byte[] sealedPublicKeyBox = SealedPublicKeyBox.Create(secretValue, publicKeyValue);

            Console.WriteLine(Convert.ToBase64String(sealedPublicKeyBox));
        }, secretOption, publicKeyOption);

        return await rootCommand.InvokeAsync(args);
    }

    static async Task<string> PerformAuthentication(string code, string clientId, string clientSecret, string redirectUrl)
    {
        OAuthToken currentOAuthToken = await manager.RequestOAuthToken(clientId, code, redirectUrl, clientSecret);

        if (currentOAuthToken != null)
        {
            var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            return JsonSerializer.Serialize(currentOAuthToken, options);
        }

        return string.Empty;
    }

    static async Task<string> PerformTokenRefresh(string refreshToken, string clientId, string clientSecret, string redirectUrl)
    {
        OAuthToken currentOAuthToken = await manager.RefreshOAuthToken(clientId, refreshToken, redirectUrl, clientSecret);

        if (currentOAuthToken != null)
        {
            var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            return JsonSerializer.Serialize(currentOAuthToken, options);
        }

        return string.Empty;
    }

    /// <summary>
    /// Acquires game stats and writes the data to independent files.
    /// </summary>
    /// <param name="token"></param>
    /// <param name="buildId">Example build ID is 222249.22.06.08.1730-0.</param>
    /// <param name="outFolder">Location of the output folder where data is written.</param>
    /// <param name="projectId">Example project ID is a9dc0785-2a99-4fec-ba6e-0216feaaf041.</param>
    /// <returns>If successful, returns true. If stats acquisition failed, returns false.</returns>
    static async Task<bool> PerformStatsAcquisition(string token, string buildId, string projectId, string outFolder)
    {
        var assetList = new List<Asset>();
        if (Directory.Exists(outFolder))
        {
            // There is already a file with the same name, assume that we're just merging data.
            try
            {
                assetList = LoadFolderStructure(outFolder);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred deserializing the content from the existing folder structure. The process will overwrite the content with a new snapshot.\nThe error was: {ex.Message}");
            }
        }

        var ticket = await manager.RequestUserToken(token);
        var haloTicket = await manager.RequestXstsToken(ticket.Token);
        var extendedTicket = await manager.RequestXstsToken(ticket.Token, false);
        var xblToken = manager.GetXboxLiveV3Token(haloTicket.DisplayClaims.Xui[0].Uhs, haloTicket.Token);
        var haloToken = await haloAuthClient.GetSpartanToken(haloTicket.Token);

        HaloInfiniteClient client = new(haloToken.Token, extendedTicket.DisplayClaims.Xui[0].Xid, string.Empty);
        var clearance = await client.SettingsGetClearance("RETAIL", "UNUSED", buildId);
        if (clearance != null)
        {
            string localClearance = clearance.FlightConfigurationId;
            client.ClearanceToken = localClearance;
            Console.WriteLine($"Your clearance is {localClearance} and it's set in the client.");
        }
        else
        {
            Console.WriteLine("Could not obtain the clearance.");
        }

        var stats = await client.HIUGCDiscoveryGetProjectWithoutVersion(projectId);

        if (stats != null)
        {
            ProcessRawAssets(stats.MapLinks, ref assetList, AssetClass.Map);
            ProcessRawAssets(stats.UgcGameVariantLinks, ref assetList, AssetClass.GameVariant);

            try
            {
                foreach (var asset in assetList)
                {
                    if (asset.Class == AssetClass.Map)
                    {
                        WriteAssetStatsToFolder(asset, outFolder, "maps");
                    }
                    else if (asset.Class == AssetClass.GameVariant)
                    {
                        WriteAssetStatsToFolder(asset, outFolder, "game_variants");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred. {ex.Message}");
                return false;
            }

            return true;
        }
        else
        {
            return false;
        }
    }

    private static void WriteAssetStatsToFolder(Asset asset, string outFolder, string container)
    {
        foreach (var version in asset.Versions)
        {
            var versionFolder = Path.Combine(outFolder, container, asset.Id, version.Metadata.Version);
            Directory.CreateDirectory(versionFolder);

            var targetStatPath = Path.Combine(versionFolder, "stats.tsv");
            List<string> lines = new();
            var header = "SnapshotTime\tRecentPlays\tAllTimePlays\tFavorites\tLikes\tBookmarks\tAverageRating\tNumberOfRatings";
            lines.Add(header);

            foreach (var stat in version.StatRecords)
            {
                var line = $"{stat.SnapshotTime}\t{stat.RecentPlays}\t{stat.AllTimePlays}\t{stat.Favorites}\t{stat.Likes}\t{stat.Bookmarks}\t{stat.AverageRating}\t{stat.NumberOfRatings}";
                lines.Add(line);
            }

            File.WriteAllLines(targetStatPath, lines);

            JsonSerializerOptions options = new JsonSerializerOptions();
            options.WriteIndented = true;

            var metadataString = JsonSerializer.Serialize(version.Metadata, options);
            File.WriteAllText(Path.Combine(versionFolder, "metadata.json"), metadataString);
        }
    }

    private static void ProcessRawAssets(AssetLink[] assets, ref List<Asset> assetList, AssetClass assetClass)
    {
        foreach (var mapAsset in assets)
        {
            var existingAsset = assetList.FirstOrDefault(x => x.Id == mapAsset.AssetId && x.Versions.Any(version => version.Metadata.Version == mapAsset.VersionId));

            Stat stat = new()
            {
                AllTimePlays = mapAsset.AssetStats.PlaysAllTime,
                AverageRating = mapAsset.AssetStats.AverageRating,
                Bookmarks = mapAsset.AssetStats.Bookmarks,
                Favorites = mapAsset.AssetStats.Favorites,
                Likes = mapAsset.AssetStats.Likes,
                NumberOfRatings = mapAsset.AssetStats.NumberOfRatings,
                RecentPlays = mapAsset.AssetStats.PlaysRecent,
                SnapshotTime = DateTime.Now
            };

            if (existingAsset != null)
            {
                var currentVersion = existingAsset.Versions.FirstOrDefault(x => x.Metadata.Version == mapAsset.VersionId);

                if (currentVersion != null)
                {
                    currentVersion.StatRecords.Add(stat);
                }
                else
                {
                    AssetVersion newVersion = new AssetVersion()
                    {
                        Metadata = new AssetMetadata()
                        {
                            Name = mapAsset.PublicName,
                            Version = mapAsset.VersionId,
                            Description = mapAsset.Description,
                            HeroImageUrl = $"{mapAsset.Files.Prefix}images/hero.png",
                            ThumbnailImageUrl = $"{mapAsset.Files.Prefix}images/thumbnail.png"
                        }
                    };

                    newVersion.StatRecords.Add(stat);
                    existingAsset.Versions.Add(newVersion);
                }
            }
            else
            {
                Asset newAsset = new Asset
                {
                    Class = assetClass,
                    Id = mapAsset.AssetId
                };

                var newVersion = new AssetVersion()
                {
                    Metadata = new AssetMetadata()
                    {
                        Name = mapAsset.PublicName,
                        Version = mapAsset.VersionId,
                        Description = mapAsset.Description,
                        HeroImageUrl = $"{mapAsset.Files.Prefix}images/hero.png",
                        ThumbnailImageUrl = $"{mapAsset.Files.Prefix}images/thumbnail.png"
                    }
                };
                newVersion.StatRecords.Add(stat);
                newAsset.Versions.Add(newVersion);
                assetList.Add(newAsset);
            }
        }
    }

    /// <summary>
    /// Loads the current folder structure in a deserialized form to operate on.
    /// </summary>
    /// <param name="outFolder">The location where the folder with asset metadata is located.</param>
    /// <returns>If successful, returns a structured list of assets and their stats.</returns>
    private static List<Asset> LoadFolderStructure(string outFolder)
    {
        List<Asset> assets = new List<Asset>();
        foreach (var containerDirectory in Directory.GetDirectories(outFolder, "*", System.IO.SearchOption.TopDirectoryOnly))
        {
            var assetClass = AssetClass.Map;
            var directoryName = Path.GetFileName(containerDirectory);

            if (directoryName.Equals("game_variants", StringComparison.InvariantCultureIgnoreCase))
                assetClass = AssetClass.GameVariant;

            foreach (var assetDirectory in Directory.GetDirectories(containerDirectory, "*", System.IO.SearchOption.TopDirectoryOnly))
            {
                Asset asset = new Asset();
                asset.Class = assetClass;

                asset.Id = Path.GetFileName(assetDirectory);

                foreach (var versionDirectory in Directory.GetDirectories(assetDirectory, "*", System.IO.SearchOption.TopDirectoryOnly))
                {
                    AssetVersion version = new();

                    var metadataPath = Path.Combine(versionDirectory, "metadata.json");
                    var statsPath = Path.Combine(versionDirectory, "stats.tsv");

                    if (File.Exists(metadataPath))
                    {
                        version.Metadata = JsonSerializer.Deserialize<AssetMetadata>(File.ReadAllText(metadataPath));

                        if (File.Exists(statsPath))
                        {
                            using (TextFieldParser parser = new TextFieldParser(statsPath))
                            {
                                parser.TextFieldType = FieldType.Delimited;
                                parser.SetDelimiters("\t");
                                while (!parser.EndOfData)
                                {
                                    string[] fields = parser.ReadFields();

                                    // Check if this is the starter header
                                    if (fields[0] != "SnapshotTime")
                                    {
                                        Stat assetStat = new()
                                        {
                                            SnapshotTime = DateTime.Parse(fields[0]),
                                            RecentPlays = Convert.ToInt64(fields[1]),
                                            AllTimePlays = Convert.ToInt64(fields[2]),
                                            Favorites = Convert.ToInt64(fields[3]),
                                            Likes = Convert.ToInt64(fields[4]),
                                            Bookmarks = Convert.ToInt64(fields[5]),
                                            AverageRating = float.Parse(fields[6]),
                                            NumberOfRatings = Convert.ToInt64(fields[7])
                                        };
                                        version.StatRecords.Add(assetStat);
                                    }
                                }
                            }
                        }
                    }

                    asset.Versions.Add(version);
                }

                assets.Add(asset);
            }

        }
        return assets;
    }
}