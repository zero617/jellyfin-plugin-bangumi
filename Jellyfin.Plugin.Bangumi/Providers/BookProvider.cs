using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bangumi.Model;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.Bangumi.Providers;

public class BookProvider : IRemoteMetadataProvider<Book, BookInfo>, IHasOrder
{
    private readonly BangumiApi _api;

    private readonly Plugin _plugin;

    public BookProvider(Plugin plugin, BangumiApi api)
    {
        _plugin = plugin;
        _api = api;
    }

    public int Order => -5;
    public string Name => Constants.ProviderName;

    public async Task<MetadataResult<Book>> GetMetadata(BookInfo info, CancellationToken token)
    {
        var result = new MetadataResult<Book> { ResultLanguage = Constants.Language };

        var subjectId = info.ProviderIds.GetOrDefault(Constants.ProviderName);
        if (string.IsNullOrEmpty(subjectId))
            return result;

        var subject = await _api.GetSubject(subjectId, token);
        if (subject == null)
            return result;

        result.Item = new Book();
        result.HasMetadata = true;

        result.Item.ProviderIds.Add(Constants.ProviderName, subjectId);
        result.Item.CommunityRating = subject.Rating?.Score;
        result.Item.Name = subject.GetName(_plugin.Configuration);
        result.Item.OriginalTitle = subject.OriginalName;
        result.Item.Overview = string.IsNullOrEmpty(subject.Summary) ? null : subject.Summary;
        result.Item.Tags = subject.PopularTags;

        if (DateTime.TryParse(subject.AirDate, out var airDate))
        {
            result.Item.PremiereDate = airDate;
            result.Item.ProductionYear = airDate.Year;
        }

        if (subject.ProductionYear?.Length == 4)
            result.Item.ProductionYear = int.Parse(subject.ProductionYear);

        return result;
    }

    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(BookInfo searchInfo, CancellationToken token)
    {
        var results = new List<RemoteSearchResult>();

        var id = searchInfo.ProviderIds.GetOrDefault(Constants.ProviderName);

        if (!string.IsNullOrEmpty(id))
        {
            var subject = await _api.GetSubject(id, token);
            if (subject == null)
                return results;
            var result = new RemoteSearchResult
            {
                Name = subject.GetName(_plugin.Configuration),
                SearchProviderName = subject.OriginalName,
                ImageUrl = subject.DefaultImage,
                Overview = subject.Summary
            };
            if (DateTime.TryParse(subject.AirDate, out var airDate))
                result.PremiereDate = airDate;
            if (subject.ProductionYear?.Length == 4)
                result.ProductionYear = int.Parse(subject.ProductionYear);
            result.SetProviderId(Constants.ProviderName, id);
            results.Add(result);
        }
        else if (!string.IsNullOrEmpty(searchInfo.Name))
        {
            var series = await _api.SearchSubject(searchInfo.Name, SubjectType.Book, token);
            series = Subject.SortBySimilarity(series, searchInfo.Name);
            foreach (var item in series)
            {
                var itemId = $"{item.Id}";
                var result = new RemoteSearchResult
                {
                    Name = item.GetName(_plugin.Configuration),
                    SearchProviderName = item.OriginalName,
                    ImageUrl = item.DefaultImage,
                    Overview = item.Summary
                };
                if (DateTime.TryParse(item.AirDate, out var airDate))
                    result.PremiereDate = airDate;
                if (item.ProductionYear?.Length == 4)
                    result.ProductionYear = int.Parse(item.ProductionYear);
                if (result.ProductionYear != null && searchInfo.Year != null)
                    if (result.ProductionYear != searchInfo.Year)
                        continue;

                result.SetProviderId(Constants.ProviderName, itemId);
                results.Add(result);
            }
        }

        return results;
    }

    public async Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken token)
    {
        return await _plugin.GetHttpClient().GetAsync(url, token);
    }
}