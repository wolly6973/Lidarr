using System.Collections.Generic;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles.TrackImport.Identification;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MediaFiles.TrackImport.Identification
{
    [TestFixture]
    public class TrackDistanceFixture : CoreTest
    {
        private Track GivenTrack(string title)
        {
            var artist = Builder<ArtistMetadata>
                .CreateNew()
                .With(x => x.Name = "artist")
                .Build();

            var mbTrack = Builder<Track>
                .CreateNew()
                .With(x => x.Title = title)
                .With(x => x.ArtistMetadata = artist)
                .Build();

            return mbTrack;
        }

        private LocalTrack GivenLocalTrack(Track track)
        {
            var fileInfo = Builder<ParsedTrackInfo>
                .CreateNew()
                .With(x => x.Title = track.Title)
                .With(x => x.CleanTitle = track.Title.CleanTrackTitle())
                .With(x => x.ArtistTitle = track.ArtistMetadata.Value.Name)
                .With(x => x.TrackNumbers = new[] { 1 })
                .With(x => x.RecordingMBId = track.ForeignRecordingId)
                .Build();

            var localTrack = Builder<LocalTrack>
                .CreateNew()
                .With(x => x.FileTrackInfo = fileInfo)
                .Build();

            return localTrack;
        }

        [Test]
        public void should_calculate_track_number_offsets_by_medium()
        {
            var tracks = new List<Track>
            {
                Builder<Track>.CreateNew().With(x => x.MediumNumber = 2).Build(),
                Builder<Track>.CreateNew().With(x => x.MediumNumber = 1).Build(),
                Builder<Track>.CreateNew().With(x => x.MediumNumber = 2).Build(),
                Builder<Track>.CreateNew().With(x => x.MediumNumber = 3).Build()
            };

            var offsets = DistanceCalculator.GetTotalTrackNumberOffsets(tracks);

            offsets[1].Should().Be(0);
            offsets[2].Should().Be(1);
            offsets[3].Should().Be(3);

            foreach (var track in tracks)
            {
                (track.AbsoluteTrackNumber + offsets[track.MediumNumber])
                    .Should().Be(DistanceCalculator.GetTotalTrackNumber(track, tracks));
            }
        }

        [Test]
        public void test_identical_tracks()
        {
            var track = GivenTrack("one");
            var localTrack = GivenLocalTrack(track);

            DistanceCalculator.TrackDistance(localTrack, track, 1, true).NormalizedDistance().Should().Be(0.0);
        }

        [Test]
        public void test_feat_removed_from_localtrack()
        {
            var track = GivenTrack("one");
            var localTrack = GivenLocalTrack(track);
            localTrack.FileTrackInfo.Title = "one (feat. two)";

            DistanceCalculator.TrackDistance(localTrack, track, 1, true).NormalizedDistance().Should().Be(0.0);
        }

        [Test]
        public void test_different_title()
        {
            var track = GivenTrack("one");
            var localTrack = GivenLocalTrack(track);
            localTrack.FileTrackInfo.CleanTitle = "foo";

            DistanceCalculator.TrackDistance(localTrack, track, 1, true).NormalizedDistance().Should().NotBe(0.0);
        }

        [Test]
        public void test_different_artist()
        {
            var track = GivenTrack("one");
            var localTrack = GivenLocalTrack(track);
            localTrack.FileTrackInfo.ArtistTitle = "foo";

            DistanceCalculator.TrackDistance(localTrack, track, 1, true).NormalizedDistance().Should().NotBe(0.0);
        }

        [Test]
        public void test_various_artists_tolerated()
        {
            var track = GivenTrack("one");
            var localTrack = GivenLocalTrack(track);
            localTrack.FileTrackInfo.ArtistTitle = "Various Artists";

            DistanceCalculator.TrackDistance(localTrack, track, 1, true).NormalizedDistance().Should().Be(0.0);
        }
    }
}
