﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bangumi.Model;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;
using Episode = MediaBrowser.Controller.Entities.TV.Episode;

namespace Jellyfin.Plugin.Bangumi.Providers;

public class EpisodeProvider : IRemoteMetadataProvider<Episode, EpisodeInfo>, IHasOrder
{
    private static readonly Regex[] NonEpisodeFileNameRegex =
    {
        new(@"[\[\(][0-9A-F]{8}[\]\)]", RegexOptions.IgnoreCase),
        new(@"S\d{2,}", RegexOptions.IgnoreCase),
        new(@"yuv[4|2|0]{3}p(10|8)?", RegexOptions.IgnoreCase),
        new(@"\d{3,4}p", RegexOptions.IgnoreCase),
        new(@"\d{3,4}x\d{3,4}", RegexOptions.IgnoreCase),
        new(@"(Hi)?10p", RegexOptions.IgnoreCase),
        new(@"(8|10)bit", RegexOptions.IgnoreCase),
        new(@"(x|h)(264|265)", RegexOptions.IgnoreCase)
    };

    private static readonly Regex[] EpisodeFileNameRegex =
    {
        new(@"\[([\d\.]{2,})\]"),
        new(@"- ?([\d\.]{2,})"),
        new(@"EP?([\d\.]{2,})", RegexOptions.IgnoreCase),
        new(@"\[([\d\.]{2,})"),
        new(@"#([\d\.]{2,})"),
        new(@"(\d{2,})")
    };

    private static readonly Regex OpeningEpisodeFileNameRegex = new(@"(NC)?OP\d");
    private static readonly Regex EndingEpisodeFileNameRegex = new(@"(NC)?ED\d");
    private static readonly Regex SpecialEpisodeFileNameRegex = new(@"[^\w](SP|OVA|OAD)\d*[^\w]");
    private static readonly Regex PreviewEpisodeFileNameRegex = new(@"[^\w]PV\d*[^\w]");

    private static readonly Regex[] AllSpecialEpisodeFileNameRegex =
    {
        SpecialEpisodeFileNameRegex,
        PreviewEpisodeFileNameRegex,
        OpeningEpisodeFileNameRegex,
        EndingEpisodeFileNameRegex
    };

    private readonly BangumiApi _api;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<EpisodeProvider> _log;

    private readonly Plugin _plugin;

    public EpisodeProvider(Plugin plugin, BangumiApi api, ILogger<EpisodeProvider> log, ILibraryManager libraryManager)
    {
        _plugin = plugin;
        _api = api;
        _log = log;
        _libraryManager = libraryManager;
    }

    public int Order => -5;
    public string Name => Constants.ProviderName;

    public async Task<MetadataResult<Episode>> GetMetadata(EpisodeInfo info, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var episode = await GetEpisode(info, token);

        _log.LogInformation("metadata for {FilePath}: {EpisodeInfo}", Path.GetFileName(info.Path), episode);

        var result = new MetadataResult<Episode> { ResultLanguage = Constants.Language };

        if (episode == null)
            return result;

        result.Item = new Episode();
        result.HasMetadata = true;
        result.Item.ProviderIds.Add(Constants.ProviderName, $"{episode.Id}");

        if (DateTime.TryParse(episode.AirDate, out var airDate))
            result.Item.PremiereDate = airDate;
        if (episode.AirDate.Length == 4)
            result.Item.ProductionYear = int.Parse(episode.AirDate);

        result.Item.Name = episode.GetName(_plugin.Configuration);
        result.Item.OriginalTitle = episode.OriginalName;
        result.Item.IndexNumber = (int)episode.Order;
        result.Item.Overview = string.IsNullOrEmpty(episode.Description) ? null : episode.Description;
        result.Item.ParentIndexNumber = 1;

        var parent = _libraryManager.FindByPath(Path.GetDirectoryName(info.Path), true);
        if (parent is Season season)
        {
            result.Item.SeasonId = season.Id;
            result.Item.ParentIndexNumber = season.IndexNumber;
        }

        if (episode.Type == EpisodeType.Normal)
            return result;

        // mark episode as special
        result.Item.ParentIndexNumber = 0;

        var series = await _api.GetSubject(episode.ParentId, token);
        if (series == null)
            return result;

        var seasonNumber = parent is Season ? parent.IndexNumber : 1;
        if (string.Compare(episode.AirDate, series.AirDate, StringComparison.Ordinal) < 0)
            result.Item.AirsBeforeEpisodeNumber = seasonNumber;
        else
            result.Item.AirsAfterSeasonNumber = seasonNumber;

        return result;
    }

    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(EpisodeInfo searchInfo, CancellationToken token)
    {
        throw new NotImplementedException();
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken token)
    {
        return _plugin.GetHttpClient().GetAsync(url, token);
    }

    private async Task<Model.Episode?> GetEpisode(EpisodeInfo info, CancellationToken token)
    {
        var fileName = Path.GetFileName(info.Path);
        if (string.IsNullOrEmpty(fileName))
            return null;

        var type = GuessEpisodeTypeFromFileName(fileName);
        var seriesId = info.SeriesProviderIds?.GetValueOrDefault(Constants.ProviderName);

        var parent = _libraryManager.FindByPath(Path.GetDirectoryName(info.Path), true);
        if (parent is Season)
        {
            var seasonId = parent.ProviderIds.GetValueOrDefault(Constants.ProviderName);
            if (!string.IsNullOrEmpty(seasonId))
                seriesId = seasonId;
        }

        if (string.IsNullOrEmpty(seriesId))
            return null;

        double? episodeIndex = info.IndexNumber;

        if (_plugin.Configuration.AlwaysReplaceEpisodeNumber)
            episodeIndex = GuessEpisodeNumber(episodeIndex, fileName);
        else if (episodeIndex is null or 0)
            episodeIndex = GuessEpisodeNumber(episodeIndex, fileName);

        var episodeId = info.ProviderIds?.GetValueOrDefault(Constants.ProviderName);
        if (!string.IsNullOrEmpty(episodeId))
        {
            var episode = await _api.GetEpisode(episodeId, token);
            if (episode == null)
                goto SkipBangumiId;

            if (_plugin.Configuration.TrustExistedBangumiId)
                return episode;

            if (episode.Type != EpisodeType.Normal || AllSpecialEpisodeFileNameRegex.Any(x => x.IsMatch(info.Path)))
                return episode;

            if ($"{episode.ParentId}" == seriesId && Math.Abs(episode.Order - episodeIndex.Value) < 0.1)
                return episode;
        }

        SkipBangumiId:
        var episodeListData = await _api.GetSubjectEpisodeList(seriesId, type, episodeIndex.Value, token);
        if (episodeListData == null)
            return null;
        if (type is null or EpisodeType.Normal)
            episodeIndex = GuessEpisodeNumber(episodeIndex, fileName, episodeListData.Max(x => x.Order));
        try
        {
            return episodeListData.OrderBy(x => x.Type).First(x => x.Order.Equals(episodeIndex));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private EpisodeType? GuessEpisodeTypeFromFileName(string fileName)
    {
        var tempName = fileName;
        foreach (var regex in NonEpisodeFileNameRegex)
        {
            if (!regex.IsMatch(tempName))
                continue;
            tempName = regex.Replace(tempName, "");
        }

        if (OpeningEpisodeFileNameRegex.IsMatch(tempName))
            return EpisodeType.Opening;
        if (EndingEpisodeFileNameRegex.IsMatch(tempName))
            return EpisodeType.Ending;
        if (SpecialEpisodeFileNameRegex.IsMatch(tempName))
            return EpisodeType.Special;
        if (PreviewEpisodeFileNameRegex.IsMatch(tempName))
            return EpisodeType.Preview;
        return null;
    }

    private double GuessEpisodeNumber(double? current, string fileName, double max = double.PositiveInfinity)
    {
        var tempName = fileName;
        var episodeIndex = current ?? 0;
        var episodeIndexFromFilename = episodeIndex;

        if (_plugin.Configuration.AlwaysGetEpisodeByAnitomySharp)
        {
            var anitomyIndex = Anitomy.ExtractEpisodeNumber(fileName);
            if (!string.IsNullOrEmpty(anitomyIndex))
                return double.Parse(anitomyIndex);
        }

        foreach (var regex in NonEpisodeFileNameRegex)
        {
            if (!regex.IsMatch(tempName))
                continue;
            tempName = regex.Replace(tempName, "");
        }

        foreach (var regex in EpisodeFileNameRegex)
        {
            if (!regex.IsMatch(tempName))
                continue;
            if (!double.TryParse(regex.Match(tempName).Groups[1].Value.Trim('.'), out var index))
                continue;
            episodeIndexFromFilename = index;
            break;
        }

        if (_plugin.Configuration.AlwaysReplaceEpisodeNumber)
        {
            _log.LogWarning("use episode index {NewIndex} from filename {FileName}", episodeIndexFromFilename, fileName);
            return episodeIndexFromFilename;
        }

        if (episodeIndexFromFilename.Equals(episodeIndex))
        {
            _log.LogInformation("use exists episode number {Index} for {FileName}", episodeIndex, fileName);
            return episodeIndex;
        }

        if (episodeIndex > max)
        {
            _log.LogWarning("file {FileName} has incorrect episode index {Index} (max {Max}), set to {NewIndex}",
                fileName, episodeIndex, max, episodeIndexFromFilename);
            return episodeIndexFromFilename;
        }

        if (episodeIndexFromFilename > 0 && episodeIndex <= 0)
        {
            _log.LogWarning("file {FileName} may has incorrect episode index {Index}, should be {NewIndex}",
                fileName, episodeIndex, episodeIndexFromFilename);
            return episodeIndexFromFilename;
        }

        _log.LogInformation("use exists episode number {Index} from file name {FileName}", episodeIndex, fileName);
        return episodeIndex;
    }
}