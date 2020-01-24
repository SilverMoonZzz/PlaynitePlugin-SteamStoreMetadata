using AngleSharp.Parser.Html;
using Playnite.Common.Web;
using Playnite.SDK;
using Playnite.SDK.Metadata;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using SteamKit2;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Windows.Controls;
using UniversalSteamMetadata.Models;
using UniversalSteamMetadata.Services;

namespace UniversalSteamMetadata
{
    public class UniversalSteamMetadata : MetadataPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private const string searchUrl = @"https://store.steampowered.com/search/?term={0}&category1=998";

        private readonly string[] backgroundUrls = new string[]
        {
            @"https://steamcdn-a.akamaihd.net/steam/apps/{0}/page.bg.jpg",
            @"https://steamcdn-a.akamaihd.net/steam/apps/{0}/page_bg_generated.jpg"
        };

        internal readonly SteamApiClient ApiClient = new SteamApiClient();
        internal UniversalSteamMetadataSettings Settings { get; set; }

        public override Guid Id { get; } = Guid.Parse("f2db8fb1-4981-4dc4-b087-05c782215b72");

        public override List<MetadataField> SupportedFields { get; } = new List<MetadataField>
        {
            MetadataField.Description,
            MetadataField.BackgroundImage,
            MetadataField.CommunityScore,
            MetadataField.CoverImage,
            MetadataField.CriticScore,
            MetadataField.Developers,
            MetadataField.Genres,
            MetadataField.Icon,
            MetadataField.Links,
            MetadataField.Publishers,
            MetadataField.ReleaseDate,
            MetadataField.Tags
        };

        public override string Name => "Steam Store";

        public UniversalSteamMetadata(IPlayniteAPI api) : base(api)
        {
            Settings = new UniversalSteamMetadataSettings(this);
        }

        public override void Dispose()
        {
            ApiClient.Logout();
        }

        public override OnDemandMetadataProvider GetMetadataProvider(MetadataRequestOptions options)
        {
            return new UniversalSteamMetadataProvider(options, this);
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return Settings;
        }

        public override UserControl GetSettingsView(bool firstRunSettings)
        {
            return new UniversalSteamMetadataSettingsView();
        }

        internal static string GetWorkshopUrl(uint appId)
        {
            return $"https://steamcommunity.com/app/{appId}/workshop/";
        }

        internal static string GetAchievementsUrl(uint appId)
        {
            return $"https://steamcommunity.com/stats/{appId}/achievements";
        }

        internal KeyValue GetAppInfo(uint appId)
        {
            var data = ApiClient.GetProductInfo(appId).GetAwaiter().GetResult();
            if (data != null)
            {
                return data;
            }

            return null;
        }

        internal StoreAppDetailsResult.AppDetails GetStoreData(uint appId)
        {
            var stringData = string.Empty;
            // Steam may return 429 if we put too many request
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    stringData = WebApiClient.GetRawStoreAppDetail(appId);
                    break;
                }
                catch (WebException e)
                {
                    if (i + 1 == 10)
                    {
                        logger.Error($"Reached download timeout for Steam store game {appId}");
                        return null;
                    }

                    if (e.Message.Contains("429"))
                    {
                        Thread.Sleep(2500);
                        continue;
                    }
                    else
                    {
                        throw;
                    }
                }
            }

            if (!string.IsNullOrEmpty(stringData))
            {
                var response = WebApiClient.ParseStoreData(appId, stringData);
                if (response.success != true)
                {
                    return null;
                }

                return response.data;
            }

            return null;
        }

        internal SteamGameMetadata DownloadGameMetadata(uint appId)
        {
            var metadata = new SteamGameMetadata();
            var productInfo = GetAppInfo(appId);
            metadata.ProductDetails = productInfo;

            try
            {
                metadata.StoreDetails = GetStoreData(appId);
            }
            catch (Exception e)
            {
                logger.Error(e, $"Failed to download Steam store metadata {appId}");
            }

            // Icon
            if (productInfo != null)
            {
                var iconRoot = @"https://steamcdn-a.akamaihd.net/steamcommunity/public/images/apps/{0}/{1}.ico";
                var icon = productInfo["common"]["clienticon"];
                var iconUrl = string.Empty;
                if (!string.IsNullOrEmpty(icon.Value))
                {
                    iconUrl = string.Format(iconRoot, appId, icon.Value);
                }
                else
                {
                    var newIcon = productInfo["common"]["icon"];
                    if (!string.IsNullOrEmpty(newIcon.Value))
                    {
                        iconRoot = @"https://steamcdn-a.akamaihd.net/steamcommunity/public/images/apps/{0}/{1}.jpg";
                        iconUrl = string.Format(iconRoot, appId, newIcon.Value);
                    }
                }

                // There might be no icon assigned to game
                if (!string.IsNullOrEmpty(iconUrl))
                {
                    metadata.Icon = new MetadataFile(iconUrl);
                }
            }

            // Image
            var useBanner = false;
            if (Settings.DownloadVerticalCovers)
            {
                var imageRoot = @"https://steamcdn-a.akamaihd.net/steam/apps/{0}/library_600x900_2x.jpg";
                var imageUrl = string.Format(imageRoot, appId);
                if (HttpDownloader.GetResponseCode(imageUrl) == HttpStatusCode.OK)
                {
                    metadata.CoverImage = new MetadataFile(imageUrl);
                }
                else
                {
                    useBanner = true;
                }
            }

            if (useBanner || !Settings.DownloadVerticalCovers)
            {
                var imageRoot = @"https://steamcdn-a.akamaihd.net/steam/apps/{0}/header.jpg";
                var imageUrl = string.Format(imageRoot, appId);
                if (HttpDownloader.GetResponseCode(imageUrl) == HttpStatusCode.OK)
                {
                    metadata.CoverImage = new MetadataFile(imageUrl);
                }
            }

            if (metadata.CoverImage == null)
            {
                if (productInfo != null)
                {
                    var imageRoot = @"https://steamcdn-a.akamaihd.net/steamcommunity/public/images/apps/{0}/{1}.jpg";
                    var image = productInfo["common"]["logo"];
                    if (!string.IsNullOrEmpty(image.Value))
                    {
                        var imageUrl = string.Format(imageRoot, appId, image.Value);
                        metadata.CoverImage = new MetadataFile(imageUrl);
                    }
                }
            }

            // Background Image
            switch (Settings.BackgroundSource)
            {
                case BackgroundSource.Image:
                    metadata.BackgroundImage = new MetadataFile(GetGameBackground(appId));
                    break;
                case BackgroundSource.StoreScreenshot:
                    if (metadata.StoreDetails != null)
                    {
                        metadata.BackgroundImage = new MetadataFile(Regex.Replace(metadata.StoreDetails.screenshots.First().path_full, "\\?.*$", ""));
                    }
                    break;
                case BackgroundSource.StoreBackground:
                    metadata.BackgroundImage = new MetadataFile(string.Format(@"https://steamcdn-a.akamaihd.net/steam/apps/{0}/page_bg_generated_v6b.jpg", appId));
                    break;
                case BackgroundSource.Banner:
                    metadata.BackgroundImage = new MetadataFile(string.Format(@"https://steamcdn-a.akamaihd.net/steam/apps/{0}/library_hero.jpg", appId));
                    break;
                default:
                    break;
            }

            return metadata;
        }

        internal string ParseDescription(string description)
        {
            return description.Replace("%CDN_HOST_MEDIA_SSL%", "steamcdn-a.akamaihd.net");
        }

        internal GameMetadata GetGameMetadata(uint appId)
        {
            var downloadedMetadata = DownloadGameMetadata(appId);
            var gameInfo = new GameInfo
            {
                Name = downloadedMetadata.ProductDetails?["common"]["name"]?.Value ?? downloadedMetadata.GameInfo.Name,
                Links = new List<Link>()
                {
                    new Link("Community Hub", $"https://steamcommunity.com/app/{appId}"),
                    new Link("Discussions", $"https://steamcommunity.com/app/{appId}/discussions/"),
                    new Link("News", $"https://store.steampowered.com/news/?appids={appId}"),
                    new Link("Store Page", $"https://store.steampowered.com/app/{appId}"),
                    new Link("PCGamingWiki", $"https://pcgamingwiki.com/api/appid.php?appid={appId}")
                }
            };

            downloadedMetadata.GameInfo = gameInfo;

            var metadata = new GameMetadata()
            {
                GameInfo = gameInfo,
                Icon = downloadedMetadata.Icon,
                CoverImage = downloadedMetadata.CoverImage,
                BackgroundImage = downloadedMetadata.BackgroundImage
            };

            if (downloadedMetadata.StoreDetails?.categories?.FirstOrDefault(a => a.id == 22) != null)
            {
                gameInfo.Links.Add(new Link("Achievements", GetAchievementsUrl(appId)));
            }

            if (downloadedMetadata.StoreDetails?.categories?.FirstOrDefault(a => a.id == 30) != null)
            {
                gameInfo.Links.Add(new Link("Workshop", GetWorkshopUrl(appId)));
            }

            if (downloadedMetadata.StoreDetails != null)
            {
                gameInfo.Description = ParseDescription(downloadedMetadata.StoreDetails.detailed_description);
                var cultInfo = new CultureInfo("en-US", false).TextInfo;
                gameInfo.ReleaseDate = downloadedMetadata.StoreDetails.release_date.date;
                gameInfo.CriticScore = downloadedMetadata.StoreDetails.metacritic?.score;

                if (downloadedMetadata.StoreDetails.publishers.HasNonEmptyItems())
                {
                    gameInfo.Publishers = new List<string>(downloadedMetadata.StoreDetails.publishers);
                }

                if (downloadedMetadata.StoreDetails.developers.HasNonEmptyItems())
                {
                    gameInfo.Developers = new List<string>(downloadedMetadata.StoreDetails.developers);
                }

                if (downloadedMetadata.StoreDetails.categories.HasItems())
                {
                    gameInfo.Tags = new List<string>(downloadedMetadata.StoreDetails.categories.Select(a => cultInfo.ToTitleCase(a.description)));
                }

                if (downloadedMetadata.StoreDetails.genres.HasItems())
                {
                    gameInfo.Genres = new List<string>(downloadedMetadata.StoreDetails.genres.Select(a => a.description));
                }
            }

            if (downloadedMetadata.ProductDetails != null)
            {
                var tasks = new List<GameAction>();
                var launchList = downloadedMetadata.ProductDetails["config"]["launch"].Children;
                foreach (var task in launchList.Skip(1))
                {
                    var properties = task["config"];
                    if (properties.Name != null)
                    {
                        if (properties["oslist"].Name != null)
                        {
                            if (properties["oslist"].Value != "windows")
                            {
                                continue;
                            }
                        }
                    }

                    // Ignore action without name  - shoudn't be visible to end user
                    if (task["description"].Name != null)
                    {
                        var newTask = new GameAction()
                        {
                            Name = task["description"].Value,
                            Arguments = task["arguments"].Value ?? string.Empty,
                            Path = task["executable"].Value,
                            IsHandledByPlugin = false,
                            WorkingDir = ExpandableVariables.InstallationDirectory
                        };

                        tasks.Add(newTask);
                    }
                }

                var manual = downloadedMetadata.ProductDetails["extended"]["gamemanualurl"];
                if (manual.Name != null)
                {
                    tasks.Add((new GameAction()
                    {
                        Name = "Manual",
                        Type = GameActionType.URL,
                        Path = manual.Value,
                        IsHandledByPlugin = false
                    }));
                }

                gameInfo.OtherActions = tasks;
            }

            return downloadedMetadata;
        }

        private string GetGameBackground(uint appId)
        {
            foreach (var url in backgroundUrls)
            {
                var bk = string.Format(url, appId);
                if (HttpDownloader.GetResponseCode(bk) == HttpStatusCode.OK)
                {
                    return bk;
                }
            }

            return null;
        }

        public static List<StoreSearchResult> GetSearchResults(string searchTerm)
        {
            var results = new List<StoreSearchResult>();
            using (var webClient = new WebClient { Encoding = Encoding.UTF8 })
            {
                var searchPageSrc = webClient.DownloadString(string.Format(searchUrl, searchTerm));
                var parser = new HtmlParser();
                var searchPage = parser.Parse(searchPageSrc);
                foreach (var gameElem in searchPage.QuerySelectorAll(".search_result_row"))
                {
                    var title = gameElem.QuerySelector(".title").InnerHtml;
                    var releaseDate = gameElem.QuerySelector(".search_released").InnerHtml;
                    if (gameElem.HasAttribute("data-ds-packageid"))
                    {
                        continue;
                    }

                    var gameId = gameElem.GetAttribute("data-ds-appid");
                    results.Add(new StoreSearchResult
                    {
                        Name = HttpUtility.HtmlDecode(title),
                        Description = HttpUtility.HtmlDecode(releaseDate),
                        GameId = uint.Parse(gameId)
                    });
                }
            }

            return results;
        }
    }
}