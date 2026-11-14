using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Dapper;
using NzbDrone.Core.Datastore;

namespace Lidarr.Api.V1.Artist
{
    public sealed class ArtistIndexSqlRow
    {
        public int Id { get; set; }
        public string SortName { get; set; }
        public long TotalMatches { get; set; }
        public int ArtistCount { get; set; }
        public int Ended { get; set; }
        public int Continuing { get; set; }
        public int Monitored { get; set; }
        public long TrackCount { get; set; }
        public long TrackFileCount { get; set; }
        public long SizeOnDisk { get; set; }
    }

    public sealed class ArtistIndexSqlResult
    {
        public int TotalArtistCount { get; set; }
        public List<int> PageIds { get; set; } = new();
        public List<ArtistIndexMatchingArtistResource> MatchingArtists { get; set; } = new();
        public List<string> Genres { get; set; } = new();
        public ArtistIndexSummaryResource Summary { get; set; } = new();
        public int TotalRecords { get; set; }
    }

    // PostgreSQL-only query path for the Artist Index. Aggregates are computed in SQL
    // before filtering, ordering, counting and paging; full Artist entities are loaded
    // only for the requested page.
    public static class ArtistIndexSqlQuery
    {
        private static readonly HashSet<string> SortKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "status", "sortName", "artistType", "qualityProfileId", "metadataProfileId",
            "monitorNewItems", "nextAlbum", "lastAlbum", "added", "albumCount",
            "trackProgress", "trackCount", "path", "sizeOnDisk", "genres", "ratings", "tags"
        };

        private const string BaseCtes = @"
WITH track_album AS (
    SELECT a.""Id"" AS ""ArtistId"", al.""Id"" AS ""AlbumId"",
           COUNT(t.""Id"")::bigint AS ""TotalTrackCount"",
           SUM(CASE WHEN (al.""ReleaseDate"" <= @now OR t.""TrackFileId"" > 0) THEN 1 ELSE 0 END)::bigint AS ""AvailableTrackCount"",
           SUM(CASE WHEN ((al.""Monitored"" = TRUE AND al.""ReleaseDate"" <= @now) OR t.""TrackFileId"" > 0) THEN 1 ELSE 0 END)::bigint AS ""TrackCount"",
           SUM(CASE WHEN t.""TrackFileId"" > 0 THEN 1 ELSE 0 END)::bigint AS ""TrackFileCount""
    FROM ""Tracks"" t
    INNER JOIN ""AlbumReleases"" ar ON ar.""Id"" = t.""AlbumReleaseId""
    INNER JOIN ""Albums"" al ON al.""Id"" = ar.""AlbumId""
    INNER JOIN ""Artists"" a ON a.""ArtistMetadataId"" = al.""ArtistMetadataId""
    WHERE ar.""Monitored"" = TRUE
    GROUP BY a.""Id"", al.""Id""
),
file_album AS (
    SELECT a.""Id"" AS ""ArtistId"", tf.""AlbumId"",
           SUM(COALESCE(tf.""Size"", 0))::bigint AS ""SizeOnDisk""
    FROM ""TrackFiles"" tf
    INNER JOIN ""Albums"" al ON al.""Id"" = tf.""AlbumId""
    INNER JOIN ""Artists"" a ON a.""ArtistMetadataId"" = al.""ArtistMetadataId""
    GROUP BY a.""Id"", tf.""AlbumId""
),
artist_stats AS (
    SELECT ta.""ArtistId"",
           COUNT(*)::integer AS ""AlbumCount"",
           SUM(ta.""TrackCount"")::bigint AS ""TrackCount"",
           SUM(ta.""TotalTrackCount"")::bigint AS ""TotalTrackCount"",
           SUM(ta.""TrackFileCount"")::bigint AS ""TrackFileCount"",
           SUM(COALESCE(fa.""SizeOnDisk"", 0))::bigint AS ""SizeOnDisk""
    FROM track_album ta
    LEFT JOIN file_album fa ON fa.""ArtistId"" = ta.""ArtistId"" AND fa.""AlbumId"" = ta.""AlbumId""
    GROUP BY ta.""ArtistId""
),
artist_dates AS (
    SELECT a.""Id"" AS ""ArtistId"",
           MIN(al.""ReleaseDate"") FILTER (WHERE al.""Monitored"" = TRUE AND al.""ReleaseDate"" > @now) AS ""NextAlbumDate"",
           MAX(al.""ReleaseDate"") FILTER (WHERE al.""Monitored"" = TRUE AND al.""ReleaseDate"" < @now) AS ""LastAlbumDate""
    FROM ""Artists"" a
    LEFT JOIN ""Albums"" al ON al.""ArtistMetadataId"" = a.""ArtistMetadataId""
    GROUP BY a.""Id""
),
artist_base AS (
    SELECT a.""Id"", a.""SortName"", a.""Monitored"", a.""MonitorNewItems"",
           a.""Path"", a.""Added"", a.""QualityProfileId"", a.""MetadataProfileId"", a.""Tags"",
           m.""Status"", m.""Type"", m.""Genres"", m.""Ratings"",
           COALESCE(s.""AlbumCount"", 0) AS ""AlbumCount"",
           COALESCE(s.""TrackCount"", 0) AS ""TrackCount"",
           COALESCE(s.""TotalTrackCount"", 0) AS ""TotalTrackCount"",
           COALESCE(s.""TrackFileCount"", 0) AS ""TrackFileCount"",
           COALESCE(s.""SizeOnDisk"", 0) AS ""SizeOnDisk"",
           d.""NextAlbumDate"", d.""LastAlbumDate""
    FROM ""Artists"" a
    INNER JOIN ""ArtistMetadata"" m ON m.""Id"" = a.""ArtistMetadataId""
    LEFT JOIN artist_stats s ON s.""ArtistId"" = a.""Id""
    LEFT JOIN artist_dates d ON d.""ArtistId"" = a.""Id""
)";

        public static ArtistIndexSqlResult Execute(IMainDatabase database, ArtistIndexPagingRequestResource request)
        {
            var now = DateTime.UtcNow;
            var parameters = new DynamicParameters();
            parameters.Add("now", now);
            var predicates = BuildPredicates(request, parameters);
            var where = predicates.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", predicates);
            var primarySort = SortKeys.Contains(request.SortKey) ? request.SortKey : "sortName";
            var secondarySort = !string.IsNullOrWhiteSpace(request.SecondarySortKey) &&
                                SortKeys.Contains(request.SecondarySortKey) &&
                                !string.Equals(primarySort, request.SecondarySortKey, StringComparison.OrdinalIgnoreCase)
                ? request.SecondarySortKey : null;
            var order = SortExpression(primarySort) + (request.SortDirection == SortDirection.Descending ? " DESC" : " ASC");
            if (secondarySort != null)
            {
                order += ", " + SortExpression(secondarySort) +
                         (request.SecondarySortDirection == SortDirection.Descending ? " DESC" : " ASC");
            }
            order += ", b.\"Id\" ASC";

            var page = Math.Max(1, request.Page);
            var pageSize = Math.Clamp(request.PageSize, 1, 500);
            parameters.Add("offset", (page - 1) * pageSize);
            parameters.Add("limit", pageSize);

            var sql = BaseCtes + @",
matched AS (SELECT b.* FROM artist_base b" + where + @"),
numbered AS (
    SELECT m.*,
           COUNT(*) OVER() AS ""TotalMatches"",
           COUNT(*) OVER()::integer AS ""ArtistCount"",
           SUM(CASE WHEN m.""Status"" = 1 THEN 1 ELSE 0 END) OVER()::integer AS ""Ended"",
           SUM(CASE WHEN m.""Status"" <> 1 THEN 1 ELSE 0 END) OVER()::integer AS ""Continuing"",
           SUM(CASE WHEN m.""Monitored"" THEN 1 ELSE 0 END) OVER()::integer AS ""MonitoredCount"",
           SUM(m.""TrackCount"") OVER()::bigint AS ""SummaryTrackCount"",
           SUM(m.""TrackFileCount"") OVER()::bigint AS ""SummaryTrackFileCount"",
           SUM(m.""SizeOnDisk"") OVER()::bigint AS ""SummarySizeOnDisk""
    FROM matched m
)
SELECT ""Id"", ""SortName"", ""TotalMatches"", ""ArtistCount"", ""Ended"", ""Continuing"",
       ""MonitoredCount"" AS ""Monitored"", ""SummaryTrackCount"" AS ""TrackCount"",
       ""SummaryTrackFileCount"" AS ""TrackFileCount"", ""SummarySizeOnDisk"" AS ""SizeOnDisk""
FROM numbered b
ORDER BY " + order + @"
OFFSET @offset LIMIT @limit";

            using var connection = database.OpenConnection();
            var rows = connection.Query<ArtistIndexSqlRow>(sql, parameters).ToList();
            var result = new ArtistIndexSqlResult
            {
                TotalArtistCount = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"Artists\""),
                TotalRecords = rows.Count == 0 ? 0 : checked((int)rows[0].TotalMatches),
                PageIds = rows.Select(x => x.Id).ToList(),
                MatchingArtists = new List<ArtistIndexMatchingArtistResource>(),
                Summary = rows.Count == 0 ? new ArtistIndexSummaryResource() : new ArtistIndexSummaryResource
                {
                    ArtistCount = rows[0].ArtistCount,
                    Ended = rows[0].Ended,
                    Continuing = rows[0].Continuing,
                    Monitored = rows[0].Monitored,
                    TrackCount = checked((int)rows[0].TrackCount),
                    TrackFileCount = checked((int)rows[0].TrackFileCount),
                    SizeOnDisk = rows[0].SizeOnDisk
                }
            };

            // Keep the lightweight alphabet-navigation list, but never materialize
            // Artist entities/resources or statistics for the entire library.
            var matchingSql = BaseCtes + @"
SELECT b.""Id"", b.""SortName""
FROM artist_base b" + where + @"
ORDER BY b.""SortName"" NULLS LAST, b.""Id""";
            result.MatchingArtists = connection.Query<ArtistIndexMatchingArtistResource>(matchingSql, parameters).ToList();
            result.Genres = connection.Query<string>(@"
SELECT DISTINCT genre
FROM ""ArtistMetadata"" m
CROSS JOIN LATERAL jsonb_array_elements_text(COALESCE(m.""Genres""::jsonb, '[]'::jsonb)) AS genre
WHERE genre IS NOT NULL AND genre <> ''
ORDER BY genre", parameters).ToList();

            return result;
        }

        private static List<string> BuildPredicates(ArtistIndexPagingRequestResource request, DynamicParameters parameters)
        {
            var filters = ParseFilters(request);
            var result = new List<string>();
            var index = 0;

            foreach (var filter in filters)
            {
                if (string.IsNullOrWhiteSpace(filter.Key) || filter.Value.ValueKind == JsonValueKind.Undefined)
                {
                    result.Add("FALSE");
                    continue;
                }

                var key = filter.Key;
                if (key == "missing")
                {
                    result.Add("(b.\"TrackCount\" > b.\"TrackFileCount\")" + (ReadBool(filter.Value) ? string.Empty : " = FALSE"));
                    continue;
                }

                var expression = key switch
                {
                    "monitored" => "b.\"Monitored\"",
                    "status" => "CASE WHEN b.\"Status\" = 1 THEN 'ended' ELSE 'continuing' END",
                    "qualityProfileId" => "b.\"QualityProfileId\"",
                    "metadataProfileId" => "b.\"MetadataProfileId\"",
                    "monitorNewItems" => "CASE b.\"MonitorNewItems\" WHEN 0 THEN 'all' WHEN 1 THEN 'none' WHEN 2 THEN 'new' ELSE b.\"MonitorNewItems\"::text END",
                    "nextAlbum" => "b.\"NextAlbumDate\"",
                    "lastAlbum" => "b.\"LastAlbumDate\"",
                    "added" => "b.\"Added\"",
                    "albumCount" => "b.\"AlbumCount\"",
                    "trackProgress" => "CASE WHEN b.\"TrackCount\" = 0 THEN 100.0 ELSE b.\"TrackFileCount\" * 100.0 / b.\"TrackCount\" END",
                    "trackCount" => "b.\"TotalTrackCount\"",
                    "path" => "COALESCE(b.\"Path\", '')",
                    "sizeOnDisk" => "b.\"SizeOnDisk\"",
                    "sortName" => "COALESCE(b.\"SortName\", '')",
                    "artistType" => "COALESCE(b.\"Type\", '')",
                    "ratings" => "COALESCE((b.\"Ratings\"::jsonb ->> 'Value')::numeric, 0) * 10",
                    "genres" => "EXISTS (SELECT 1 FROM jsonb_array_elements_text(COALESCE(b.\"Genres\"::jsonb, '[]'::jsonb)) g WHERE lower(g) = lower(@p" + index + "))",
                    "tags" => "@p" + index + " = ANY(COALESCE(b.\"Tags\", ARRAY[]::integer[]))",
                    _ => null
                };

                if (expression == null)
                {
                    result.Add("FALSE");
                    continue;
                }

                var values = filter.Value.ValueKind == JsonValueKind.Array
                    ? filter.Value.EnumerateArray().ToList()
                    : new List<JsonElement> { filter.Value };
                var alternatives = new List<string>();
                foreach (var value in values)
                {
                    var parameter = "p" + index++;
                    object parameterValue = value.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.Number when value.TryGetInt64(out var n) => n,
                        JsonValueKind.Number => value.GetDouble(),
                        JsonValueKind.String => value.GetString(),
                        JsonValueKind.Object when value.TryGetProperty("value", out var v) => v.ToString(),
                        _ => value.ToString()
                    };
                    parameters.Add(parameter, parameterValue);
                    var op = filter.Type ?? "equal";
                    var comparison = op switch
                    {
                        "notEqual" => "<>",
                        "greaterThan" => ">",
                        "greaterThanOrEqual" => ">=",
                        "lessThan" => "<",
                        "lessThanOrEqual" => "<=",
                        "contains" or "notContains" => "ILIKE",
                        "startsWith" or "notStartsWith" => "ILIKE",
                        "endsWith" or "notEndsWith" => "ILIKE",
                        _ => "="
                    };
                    var rhs = "@" + parameter;
                    if (op == "contains" || op == "notContains") rhs = "'%' || @" + parameter + " || '%'";
                    if (op == "startsWith" || op == "notStartsWith") rhs = "@" + parameter + " || '%'";
                    if (op == "endsWith" || op == "notEndsWith") rhs = "'%' || @" + parameter;
                    if (op == "notContains" || op == "notStartsWith" || op == "notEndsWith") comparison = "NOT ILIKE";
                    var clause = expression.Replace("@p" + (index - 1), "@" + parameter) + " " + comparison + " " + rhs;
                    if (key == "genres") clause = "EXISTS (SELECT 1 FROM jsonb_array_elements_text(COALESCE(b.\"Genres\"::jsonb, '[]'::jsonb)) g WHERE lower(g) = lower(@" + parameter + "))";
                    if (key == "tags") clause = "@" + parameter + " = ANY(COALESCE(b.\"Tags\", ARRAY[]::integer[]))";
                    alternatives.Add(clause);
                }

                if (alternatives.Count > 0)
                {
                    var negative = filter.Type == "notEqual" || filter.Type == "notContains" ||
                                   filter.Type == "notStartsWith" || filter.Type == "notEndsWith";
                    result.Add("(" + string.Join(negative ? " AND " : " OR ", alternatives) + ")");
                }
            }

            return result;
        }

        private static string SortExpression(string key)
        {
            return key switch
            {
                "status" => "(CASE WHEN b.\"Monitored\" THEN 2 ELSE 0 END + CASE WHEN b.\"Status\" <> 1 THEN 1 ELSE 0 END)",
                "sortName" => "b.\"SortName\"",
                "artistType" => "b.\"Type\"",
                "qualityProfileId" => "b.\"QualityProfileId\"",
                "metadataProfileId" => "b.\"MetadataProfileId\"",
                "monitorNewItems" => "b.\"MonitorNewItems\"",
                "nextAlbum" => "COALESCE(b.\"NextAlbumDate\", TIMESTAMP '1000-01-01')",
                "lastAlbum" => "COALESCE(b.\"LastAlbumDate\", TIMESTAMP '1000-01-01')",
                "added" => "b.\"Added\"",
                "albumCount" => "b.\"AlbumCount\"",
                "trackProgress" => "(CASE WHEN b.\"TrackCount\" = 0 THEN 100.0 ELSE b.\"TrackFileCount\" * 100.0 / b.\"TrackCount\" END + b.\"TrackCount\" / 1000000.0)",
                "trackCount" => "b.\"TotalTrackCount\"",
                "path" => "b.\"Path\"",
                "sizeOnDisk" => "b.\"SizeOnDisk\"",
                "genres" => "b.\"Genres\"::text",
                "ratings" => "b.\"Ratings\"::text",
                "tags" => "b.\"Tags\"::text",
                _ => "b.\"SortName\""
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
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
            }

            try
            {
                return JsonSerializer.Deserialize<List<ArtistIndexFilterResource>>(request.Filters,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
            }
            catch (JsonException)
            {
                throw new ArgumentException("The artist index filters query parameter must be a JSON array.");
            }
        }

        private static bool ReadBool(JsonElement value)
        {
            return value.ValueKind == JsonValueKind.True ||
                   (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed) && parsed);
        }
    }
}