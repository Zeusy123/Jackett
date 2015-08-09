﻿using Jackett.Models;
using Jackett.Services;
using Jackett.Utils;
using Jackett.Utils.Clients;
using Newtonsoft.Json.Linq;
using NLog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using Jackett.Models.IndexerConfig;

namespace Jackett.Indexers
{
    public class Strike : BaseIndexer, IIndexer
    {
        readonly static string defaultSiteLink = "https://getstrike.net/";

        private Uri BaseUri
        {
            get { return new Uri(configData.Url.Value); }
            set { configData.Url.Value = value.ToString(); }
        }

        private string SearchUrl { get { return BaseUri + "api/v2/torrents/search/?category=TV&phrase={0}"; } }
        private string DownloadUrl { get { return BaseUri + "torrents/api/download/{0}.torrent"; } }

        new ConfigurationDataUrl configData
        {
            get { return (ConfigurationDataUrl)base.configData; }
            set { base.configData = value; }
        }


        public Strike(IIndexerManagerService i, Logger l, IWebClient wc, IProtectionService ps)
            : base(name: "Strike",
                description: "Torrent search engine",
                link: defaultSiteLink,
                caps: TorznabCapsUtil.CreateDefaultTorznabTVCaps(),
                manager: i,
                client: wc,
                logger: l,
                p: ps,
                configData: new ConfigurationDataUrl(defaultSiteLink))
        {
        }

        public async Task ApplyConfiguration(JToken configJson)
        {
            configData.LoadValuesFromJson(configJson);
            var releases = await PerformQuery(new TorznabQuery());

            await ConfigureIfOK(string.Empty, releases.Count() > 0, () =>
            {
                throw new Exception("Could not find releases from this URL");
            });
        }

        // Override to load legacy config format
        public override void LoadFromSavedConfiguration(JToken jsonConfig)
        {
            if (jsonConfig is JObject)
            {
                BaseUri = new Uri(jsonConfig.Value<string>("base_url"));
                SaveConfig();
                IsConfigured = true;
                return;
            }

            base.LoadFromSavedConfiguration(jsonConfig);
        }

        public async Task<IEnumerable<ReleaseInfo>> PerformQuery(TorznabQuery query)
        {
            List<ReleaseInfo> releases = new List<ReleaseInfo>();

            var searchTerm = string.IsNullOrEmpty(query.SanitizedSearchTerm) ? "2015" : query.SanitizedSearchTerm;

            var searchString = searchTerm + " " + query.GetEpisodeSearchString();
            var episodeSearchUrl = string.Format(SearchUrl, HttpUtility.UrlEncode(searchString.Trim()));
            var results = await RequestStringWithCookiesAndRetry(episodeSearchUrl, string.Empty);
            try
            {
                var jResults = JObject.Parse(results.Content);
                foreach (JObject result in (JArray)jResults["torrents"])
                {
                    var release = new ReleaseInfo();

                    release.MinimumRatio = 1;
                    release.MinimumSeedTime = 172800;

                    release.Title = (string)result["torrent_title"];
                    release.Description = release.Title;
                    release.Seeders = (int)result["seeds"];
                    release.Peers = (int)result["leeches"] + release.Seeders;
                    release.Size = (long)result["size"];

                    // "Apr  2, 2015", "Apr 12, 2015" (note the spacing)
                    // some are unix timestamps, some are not.. :/
                    var dateString = string.Join(" ", ((string)result["upload_date"]).Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
                    float dateVal;
                    if (ParseUtil.TryCoerceFloat(dateString, out dateVal))
                        release.PublishDate = DateTimeUtil.UnixTimestampToDateTime(dateVal);
                    else
                        release.PublishDate = DateTime.ParseExact(dateString, "MMM d, yyyy", CultureInfo.InvariantCulture);

                    release.Guid = new Uri((string)result["page"]);
                    release.Comments = release.Guid;

                    release.InfoHash = (string)result["torrent_hash"];
                    release.MagnetUri = new Uri((string)result["magnet_uri"]);
                    release.Link = new Uri(string.Format(DownloadUrl, release.InfoHash));

                    releases.Add(release);
                }
            }
            catch (Exception ex)
            {
                OnParseError(results.Content, ex);
            }

            return releases;
        }

        public override Task<byte[]> Download(Uri link)
        {
            throw new NotImplementedException();
        }
    }
}
