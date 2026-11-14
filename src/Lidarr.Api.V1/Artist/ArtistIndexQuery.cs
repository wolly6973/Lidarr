using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Lidarr.Http;
using NzbDrone.Core.Datastore;

namespace Lidarr.Api.V1.Artist
{
    public class ArtistIndexPagingRequestResource : PagingRequestResource
    {
        public string FilterKey { get; set; }
        public string Filters { get; set; }
        public string SecondarySortKey { get; set; }
        public SortDirection? SecondarySortDirection { get; set; }
    }

    public class ArtistIndexPagingResource : PagingResource<ArtistResource>
    {
        public List<int> MatchingIds { get; set; } = new();
    }

    public class ArtistIndexFilterResource
    {
        public string Key { get; set; }
        public JsonElement Value { get; set; }
        public string Type { get; set; } = "equal";
    }

    public static class ArtistIndexQuery
    {
        private static readonly HashSet<string> SortKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "status", "sortName", "artistType", "qualityProfileId", "metadataProfileId",
            "monitorNewItems", "nextAlbum", "lastAlbum", "added", "albumCount",
            "trackProgress", "trackCount", "path", "sizeOnDisk", "genres", "ratings", "tags"
        };

        public static ArtistIndexPagingResource Apply(
            PagingResource<ArtistResource> paging,
            ArtistIndexPagingRequestResource request,
            IEnumerable<ArtistResource> source)
        {
            var filters = ParseFilters(request);
            var matching = source.Where(artist => filters.All(filter => Matches(artist, filter))).ToList();

            var sortKey = SortKeys.Contains(request.SortKey) ? request.SortKey : "sortName";
            var descending = request.SortDirection == SortDirection.Descending;
            IOrderedEnumerable<ArtistResource> ordered = Order(matching, sortKey, descending);

            if (!string.IsNullOrWhiteSpace(request.SecondarySortKey) &&
                SortKeys.Contains(request.SecondarySortKey) &&
                !string.Equals(sortKey, request.SecondarySortKey, StringComparison.OrdinalIgnoreCase))
            {
                ordered = Order(ordered, request.SecondarySortKey,
                    request.SecondarySortDirection == SortDirection.Descending);
            }

            ordered = ordered.ThenBy(x => x.Id);

            var page = Math.Max(1, paging.Page);
            var pageSize = Math.Clamp(paging.PageSize, 1, 500);
            paging.Page = page;
            paging.PageSize = pageSize;
            paging.SortKey = sortKey;
            return new ArtistIndexPagingResource
            {
                Page = page,
                PageSize = pageSize,
                SortKey = sortKey,
                SortDirection = paging.SortDirection,
                TotalRecords = matching.Count,
                MatchingIds = ordered.Select(x => x.Id).ToList(),
                Records = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList()
            };
        }

        private static List<ArtistIndexFilterResource> ParseFilters(ArtistIndexPagingRequestResource request)
        {
            if (string.IsNullOrWhiteSpace(request.Filters))
            {
                var builtIn = request.FilterKey switch
                {
                    "monitored" => "[{\"key\":\"monitored\",\"value\":true,\"type\":\"equal\"}]",
                    "unmonitored" => "[{\"key\":\"monitored\",\"value\":false,\"type\":\"equal\"}]",
                    "continuing" => "[{\"key\":\"status\",\"value\":\"continuing\",\"type\":\"equal\"}]",
                    "ended" => "[{\"key\":\"status\",\"value\":\"ended\",\"type\":\"equal\"}]",
                    "missing" => "[{\"key\":\"missing\",\"value\":true,\"type\":\"equal\"}]",
                    _ => "[]"
                };

                return JsonSerializer.Deserialize<List<ArtistIndexFilterResource>>(builtIn,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ??
                    new List<ArtistIndexFilterResource>();
            }

            try
            {
                return JsonSerializer.Deserialize<List<ArtistIndexFilterResource>>(request.Filters,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ??
                    new List<ArtistIndexFilterResource>();
            }
            catch (JsonException)
            {
                throw new ArgumentException("The artist index filters query parameter must be a JSON array.");
            }
        }

        private static bool Matches(ArtistResource artist, ArtistIndexFilterResource filter)
        {
            if (string.IsNullOrWhiteSpace(filter.Key) || filter.Value.ValueKind == JsonValueKind.Undefined)
            {
                return false;
            }

            var value = filter.Value;
            var key = filter.Key;
            object item = key switch
            {
                "missing" => (artist.Statistics?.TrackCount ?? 0) > (artist.Statistics?.TrackFileCount ?? 0),
                "monitored" => artist.Monitored,
                "status" => artist.Status.ToString().ToLowerInvariant(),
                "qualityProfileId" => artist.QualityProfileId,
                "metadataProfileId" => artist.MetadataProfileId,
                "monitorNewItems" => artist.MonitorNewItems.ToString().ToLowerInvariant(),
                "nextAlbum" => artist.NextAlbum?.ReleaseDate,
                "lastAlbum" => artist.LastAlbum?.ReleaseDate,
                "added" => artist.Added,
                "albumCount" => artist.Statistics?.AlbumCount ?? 0,
                "trackProgress" => TrackProgress(artist),
                "trackCount" => artist.Statistics?.TotalTrackCount ?? 0,
                "path" => artist.Path ?? string.Empty,
                "sizeOnDisk" => artist.Statistics?.SizeOnDisk ?? 0L,
                "genres" => artist.Genres ?? new List<string>(),
                "ratings" => (artist.Ratings?.Value ?? 0) * 10,
                "tags" => artist.Tags ?? new HashSet<int>(),
                _ => GetSimpleValue(artist, key)
            };

            if (value.ValueKind == JsonValueKind.Array)
            {
                var values = value.EnumerateArray().ToList();
                if (IsNegativeOperator(filter.Type))
                {
                    return values.All(v => Compare(item, v, filter.Type));
                }

                return values.Any(v => Compare(item, v, filter.Type));
            }

            return Compare(item, value, filter.Type);
        }

        private static object GetSimpleValue(ArtistResource artist, string key)
        {
            return key switch
            {
                "sortName" => artist.SortName ?? string.Empty,
                "artistType" => artist.ArtistType ?? string.Empty,
                _ => null
            };
        }

        private static double TrackProgress(ArtistResource artist)
        {
            var trackCount = artist.Statistics?.TrackCount ?? 0;
            var trackFileCount = artist.Statistics?.TrackFileCount ?? 0;
            return trackCount == 0 ? 100 : trackFileCount / (double)trackCount * 100;
        }

        private static bool IsNegativeOperator(string type)
        {
            return type == "notContains" || type == "notEqual" ||
                   type == "notStartsWith" || type == "notEndsWith";
        }

        private static bool Compare(object item, JsonElement value, string type)
        {
            if (item is null)
            {
                return type == "notEqual" || type == "notContains";
            }

            if (item is IEnumerable<string> strings)
            {
                var expected = ReadString(value);
                var contains = strings.Any(x => string.Equals(x, expected, StringComparison.OrdinalIgnoreCase));
                return type == "notContains" || type == "notEqual" ? !contains : contains;
            }

            if (item is IEnumerable<int> integers)
            {
                var expected = ReadInt(value);
                var contains = integers.Contains(expected);
                return type == "notContains" || type == "notEqual" ? !contains : contains;
            }

            if (item is bool boolean)
            {
                var expected = value.ValueKind == JsonValueKind.True ||
                               (value.ValueKind == JsonValueKind.String &&
                                bool.TryParse(value.GetString(), out var parsed) && parsed);
                return type == "notEqual" ? boolean != expected : boolean == expected;
            }

            if (item is DateTime date)
            {
                if (type == "inLast" || type == "notInLast" ||
                    type == "inNext" || type == "notInNext")
                {
                    if (value.ValueKind != JsonValueKind.Object ||
                        !value.TryGetProperty("time", out var unit) ||
                        !value.TryGetProperty("value", out var amount) ||
                        !amount.TryGetDouble(out var number))
                    {
                        return false;
                    }

                    var boundary = AddDateOffset(DateTime.UtcNow, unit.GetString(), number *
                        ((type == "inLast" || type == "notInLast") ? -1 : 1));
                    return type switch
                    {
                        "inLast" => date > boundary && date < DateTime.UtcNow,
                        "notInLast" => date <= boundary,
                        "inNext" => date > DateTime.UtcNow && date < boundary,
                        "notInNext" => date >= boundary,
                        _ => false
                    };
                }

                if (!DateTime.TryParse(ReadString(value), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var expectedDate))
                {
                    return false;
                }

                return CompareValues(date, expectedDate, type);
            }

            if (item is IConvertible && value.ValueKind == JsonValueKind.Number)
            {
                var left = Convert.ToDouble(item, CultureInfo.InvariantCulture);
                var right = value.GetDouble();
                return CompareValues(left, right, type);
            }

            var actual = Convert.ToString(item, CultureInfo.InvariantCulture) ?? string.Empty;
            var expectedText = ReadString(value);
            var comparison = StringComparison.OrdinalIgnoreCase;

            return type switch
            {
                "contains" => actual.Contains(expectedText, comparison),
                "notContains" => !actual.Contains(expectedText, comparison),
                "startsWith" => actual.StartsWith(expectedText, comparison),
                "notStartsWith" => !actual.StartsWith(expectedText, comparison),
                "endsWith" => actual.EndsWith(expectedText, comparison),
                "notEndsWith" => !actual.EndsWith(expectedText, comparison),
                "notEqual" => !string.Equals(actual, expectedText, comparison),
                _ => string.Equals(actual, expectedText, comparison)
            };
        }

        private static bool CompareValues<T>(T left, T right, string type) where T : IComparable<T>
        {
            var comparison = left.CompareTo(right);
            return type switch
            {
                "notEqual" => comparison != 0,
                "greaterThan" => comparison > 0,
                "greaterThanOrEqual" => comparison >= 0,
                "lessThan" => comparison < 0,
                "lessThanOrEqual" => comparison <= 0,
                _ => comparison == 0
            };
        }

        private static DateTime AddDateOffset(DateTime date, string unit, double value)
        {
            return unit switch
            {
                "minute" or "minutes" => date.AddMinutes(value),
                "hour" or "hours" => date.AddHours(value),
                "day" or "days" => date.AddDays(value),
                "week" or "weeks" => date.AddDays(value * 7),
                "month" or "months" => date.AddMonths((int)value),
                "year" or "years" => date.AddYears((int)value),
                _ => date
            };
        }

        private static string ReadString(JsonElement value)
        {
            return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
        }

        private static int ReadInt(JsonElement value)
        {
            return value.ValueKind == JsonValueKind.Number ? value.GetInt32() :
                int.TryParse(ReadString(value), out var result) ? result : int.MinValue;
        }

        private static IOrderedEnumerable<ArtistResource> Order(
            IEnumerable<ArtistResource> source, string key, bool descending)
        {
            Func<ArtistResource, object> selector = key switch
            {
                "status" => x => (x.Monitored ? 2 : 0) + (x.Status.ToString().Equals("continuing", StringComparison.OrdinalIgnoreCase) ? 1 : 0),
                "sortName" => x => x.SortName ?? string.Empty,
                "artistType" => x => x.ArtistType ?? string.Empty,
                "qualityProfileId" => x => x.QualityProfileId,
                "metadataProfileId" => x => x.MetadataProfileId,
                "monitorNewItems" => x => x.MonitorNewItems.ToString(),
                "nextAlbum" => x => x.NextAlbum?.ReleaseDate ?? new DateTime(1000, 1, 1),
                "lastAlbum" => x => x.LastAlbum?.ReleaseDate ?? new DateTime(1000, 1, 1),
                "added" => x => x.Added,
                "albumCount" => x => x.Statistics?.AlbumCount ?? 0,
                "trackProgress" => x => TrackProgress(x) + (x.Statistics?.TrackCount ?? 0) / 1000000d,
                "trackCount" => x => x.Statistics?.TotalTrackCount ?? 0,
                "path" => x => x.Path ?? string.Empty,
                "sizeOnDisk" => x => x.Statistics?.SizeOnDisk ?? 0L,
                "genres" => x => string.Join(", ", (x.Genres ?? new List<string>()).OrderBy(g => g, StringComparer.OrdinalIgnoreCase)),
                "ratings" => x => x.Ratings?.Value ?? 0,
                "tags" => x => string.Join(",", (x.Tags ?? new HashSet<int>()).OrderBy(t => t)),
                _ => x => x.SortName ?? string.Empty
            };

            return descending
                ? source.OrderByDescending(selector)
                : source.OrderBy(selector);
        }

        private static IOrderedEnumerable<ArtistResource> Order(
            IOrderedEnumerable<ArtistResource> source, string key, bool descending)
        {
            Func<ArtistResource, object> selector = key switch
            {
                "sortName" => x => x.SortName ?? string.Empty,
                "added" => x => x.Added,
                "albumCount" => x => x.Statistics?.AlbumCount ?? 0,
                "trackProgress" => x => TrackProgress(x) + (x.Statistics?.TrackCount ?? 0) / 1000000d,
                "trackCount" => x => x.Statistics?.TotalTrackCount ?? 0,
                "sizeOnDisk" => x => x.Statistics?.SizeOnDisk ?? 0L,
                "qualityProfileId" => x => x.QualityProfileId,
                "metadataProfileId" => x => x.MetadataProfileId,
                "nextAlbum" => x => x.NextAlbum?.ReleaseDate ?? new DateTime(1000, 1, 1),
                "lastAlbum" => x => x.LastAlbum?.ReleaseDate ?? new DateTime(1000, 1, 1),
                "artistType" => x => x.ArtistType ?? string.Empty,
                "path" => x => x.Path ?? string.Empty,
                "ratings" => x => x.Ratings?.Value ?? 0,
                "tags" => x => string.Join(",", (x.Tags ?? new HashSet<int>()).OrderBy(t => t)),
                "genres" => x => string.Join(", ", (x.Genres ?? new List<string>()).OrderBy(g => g, StringComparer.OrdinalIgnoreCase)),
                "monitorNewItems" => x => x.MonitorNewItems.ToString(),
                "status" => x => (x.Monitored ? 2 : 0) + (x.Status.ToString().Equals("continuing", StringComparison.OrdinalIgnoreCase) ? 1 : 0),
                _ => x => x.SortName ?? string.Empty
            };

            return descending ? source.ThenByDescending(selector) : source.ThenBy(selector);
        }
    }
}
