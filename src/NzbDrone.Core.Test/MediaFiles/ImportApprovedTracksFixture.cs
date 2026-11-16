using System.Collections.Generic;
using System.IO;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.MediaFiles.TrackImport;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class ImportApprovedTracksFixture : CoreTest<ImportApprovedTracks>
    {
        private List<ImportDecision<LocalTrack>> _rejectedDecisions;
        private List<ImportDecision<LocalTrack>> _approvedDecisions;

        private DownloadClientItem _downloadClientItem;
        private DownloadClientItemClientInfo _clientInfo;

        [SetUp]
        public void Setup()
        {
            _rejectedDecisions = new List<ImportDecision<LocalTrack>>();
            _approvedDecisions = new List<ImportDecision<LocalTrack>>();

            var artist = Builder<Artist>.CreateNew()
                                        .With(e => e.QualityProfile = new QualityProfile { Items = Qualities.QualityFixture.GetDefaultQualities() })
                                        .With(s => s.Path = @"C:\Test\Music\Alien Ant Farm".AsOsAgnostic())
                                        .Build();

            var album = Builder<Album>.CreateNew()
                .With(e => e.Artist = artist)
                .Build();

            var release = Builder<AlbumRelease>.CreateNew()
                .With(e => e.AlbumId = album.Id)
                .With(e => e.Monitored = true)
                .Build();

            album.AlbumReleases = new List<AlbumRelease> { release };

            var tracks = Builder<Track>.CreateListOfSize(5)
                                           .Build();

            _rejectedDecisions.Add(new ImportDecision<LocalTrack>(new LocalTrack(), new Rejection("Rejected!")));
            _rejectedDecisions.Add(new ImportDecision<LocalTrack>(new LocalTrack(), new Rejection("Rejected!")));
            _rejectedDecisions.Add(new ImportDecision<LocalTrack>(new LocalTrack(), new Rejection("Rejected!")));

            foreach (var track in tracks)
            {
                _approvedDecisions.Add(new ImportDecision<LocalTrack>(
                                           new LocalTrack
                                           {
                                               Artist = artist,
                                               Album = album,
                                               Release = release,
                                               Tracks = new List<Track> { track },
                                               Path = Path.Combine(artist.Path, "Alien Ant Farm - 01 - Pilot.mp3"),
                                               Quality = new QualityModel(Quality.MP3_256),
                                               FileTrackInfo = new ParsedTrackInfo
                                               {
                                                   ReleaseGroup = "DRONE"
                                               }
                                           }));
            }

            Mocker.GetMock<IUpgradeMediaFiles>()
                  .Setup(s => s.UpgradeTrackFile(It.IsAny<TrackFile>(), It.IsAny<LocalTrack>(), It.IsAny<bool>()))
                  .Returns(new TrackFileMoveResult());

            _clientInfo = Builder<DownloadClientItemClientInfo>.CreateNew().Build();
            _downloadClientItem = Builder<DownloadClientItem>.CreateNew().With(x => x.DownloadClientInfo = _clientInfo).Build();

            Mocker.GetMock<IMediaFileService>()
                .Setup(s => s.GetFilesByAlbum(It.IsAny<int>()))
                .Returns(new List<TrackFile>());

            Mocker.GetMock<IMediaFileService>()
                .Setup(s => s.GetFileWithPath(It.IsAny<List<string>>()))
                .Returns(new List<TrackFile>());

            Mocker.GetMock<IMediaFileService>()
                .Setup(s => s.GetFilesByAlbums(It.IsAny<List<int>>()))
                .Returns(new List<TrackFile>());

            Mocker.GetMock<IAlbumService>()
                .Setup(s => s.GetAlbums(It.IsAny<IEnumerable<int>>()))
                .Returns((IEnumerable<int> albumIds) => albumIds
                    .Where(id => id == album.Id)
                    .Select(_ => album)
                    .ToList());
        }

        [Test]
        public void should_not_import_any_if_there_are_no_approved_decisions()
        {
            Subject.Import(_rejectedDecisions, false).Where(i => i.Result == ImportResultType.Imported).Should().BeEmpty();

            Mocker.GetMock<IMediaFileService>().Verify(v => v.Add(It.IsAny<TrackFile>()), Times.Never());
        }

        [Test]
        public void should_import_each_approved()
        {
            Subject.Import(_approvedDecisions, false).Should().HaveCount(5);
        }

        [Test]
        public void should_only_import_approved()
        {
            var all = new List<ImportDecision<LocalTrack>>();
            all.AddRange(_rejectedDecisions);
            all.AddRange(_approvedDecisions);

            var result = Subject.Import(all, false);

            result.Should().HaveCount(all.Count);
            result.Where(i => i.Result == ImportResultType.Imported).Should().HaveCount(_approvedDecisions.Count);
        }

        [Test]
        public void should_only_import_each_track_once()
        {
            var all = new List<ImportDecision<LocalTrack>>();
            all.AddRange(_approvedDecisions);
            all.Add(new ImportDecision<LocalTrack>(_approvedDecisions.First().Item));

            var result = Subject.Import(all, false);

            result.Where(i => i.Result == ImportResultType.Imported).Should().HaveCount(_approvedDecisions.Count);
        }

        [Test]
        public void should_move_new_downloads()
        {
            Subject.Import(new List<ImportDecision<LocalTrack>> { _approvedDecisions.First() }, true);

            Mocker.GetMock<IUpgradeMediaFiles>()
                  .Verify(v => v.UpgradeTrackFile(It.IsAny<TrackFile>(), _approvedDecisions.First().Item, false),
                          Times.Once());
        }

        [Test]
        public void should_publish_TrackImportedEvent_for_new_downloads()
        {
            Subject.Import(new List<ImportDecision<LocalTrack>> { _approvedDecisions.First() }, true);

            Mocker.GetMock<IEventAggregator>()
                .Verify(v => v.PublishEvent(It.IsAny<TrackImportedEvent>()), Times.Once());
        }

        [Test]
        public void should_not_move_existing_files()
        {
            var track = _approvedDecisions.First();
            track.Item.ExistingFile = true;
            Subject.Import(new List<ImportDecision<LocalTrack>> { track }, false);

            Mocker.GetMock<IUpgradeMediaFiles>()
                  .Verify(v => v.UpgradeTrackFile(It.IsAny<TrackFile>(), _approvedDecisions.First().Item, false),
                          Times.Never());
        }

        [Test]
        public void should_import_larger_files_first()
        {
            var fileDecision = _approvedDecisions.First();
            fileDecision.Item.Size = 1.Gigabytes();

            var sampleDecision = new ImportDecision<LocalTrack>(
                new LocalTrack
                {
                    Artist = fileDecision.Item.Artist,
                    Album = fileDecision.Item.Album,
                    Tracks = new List<Track> { fileDecision.Item.Tracks.First() },
                    Path = @"C:\Test\Music\Alien Ant Farm\Alien Ant Farm - 01 - Pilot.mp3".AsOsAgnostic(),
                    Quality = new QualityModel(Quality.MP3_256),
                    Size = 80.Megabytes()
                });

            var all = new List<ImportDecision<LocalTrack>>();
            all.Add(fileDecision);
            all.Add(sampleDecision);

            var results = Subject.Import(all, false);

            results.Should().HaveCount(all.Count);
            results.Should().ContainSingle(d => d.Result == ImportResultType.Imported);
            results.Should().ContainSingle(d => d.Result == ImportResultType.Imported && d.ImportDecision.Item.Size == fileDecision.Item.Size);
        }

        [Test]
        public void should_copy_when_cannot_move_files_downloads()
        {
            Subject.Import(new List<ImportDecision<LocalTrack>> { _approvedDecisions.First() }, true, new DownloadClientItem { Title = "Alien.Ant.Farm-Truant", CanMoveFiles = false, DownloadClientInfo = _clientInfo });

            Mocker.GetMock<IUpgradeMediaFiles>()
                  .Verify(v => v.UpgradeTrackFile(It.IsAny<TrackFile>(), _approvedDecisions.First().Item, true), Times.Once());
        }

        [Test]
        public void should_use_override_importmode()
        {
            Subject.Import(new List<ImportDecision<LocalTrack>> { _approvedDecisions.First() }, true, new DownloadClientItem { Title = "Alien.Ant.Farm-Truant", CanMoveFiles = false, DownloadClientInfo = _clientInfo }, ImportMode.Move);

            Mocker.GetMock<IUpgradeMediaFiles>()
                  .Verify(v => v.UpgradeTrackFile(It.IsAny<TrackFile>(), _approvedDecisions.First().Item, false), Times.Once());
        }

        [Test]
        public void should_delete_existing_trackfiles_with_the_same_path()
        {
            var previousFile = Builder<TrackFile>.CreateNew()
                .With(file => file.Path = _approvedDecisions.First().Item.Path)
                .Build();

            Mocker.GetMock<IMediaFileService>()
                .Setup(s => s.GetFileWithPath(It.IsAny<List<string>>()))
                .Returns(new List<TrackFile> { previousFile });

            var track = _approvedDecisions.First();
            track.Item.ExistingFile = true;
            Subject.Import(new List<ImportDecision<LocalTrack>> { track }, false);

            Mocker.GetMock<IMediaFileService>()
                .Verify(v => v.Delete(It.IsAny<TrackFile>(), DeleteMediaFileReason.ManualOverride), Times.Once());
        }

        [Test]
        public void should_lookup_replaced_album_files_in_one_batch()
        {
            var firstDecision = _approvedDecisions.First();
            var secondAlbum = Builder<Album>.CreateNew()
                .With(album => album.Artist = firstDecision.Item.Artist)
                .With(album => album.AlbumReleases = new List<AlbumRelease>())
                .Build();
            var secondRelease = Builder<AlbumRelease>.CreateNew()
                .With(release => release.AlbumId = secondAlbum.Id)
                .Build();
            secondAlbum.AlbumReleases = new List<AlbumRelease> { secondRelease };

            var secondDecision = new ImportDecision<LocalTrack>(
                new LocalTrack
                {
                    Artist = firstDecision.Item.Artist,
                    Album = secondAlbum,
                    Release = secondRelease,
                    Tracks = new List<Track> { Builder<Track>.CreateNew().Build() },
                    Path = Path.Combine(firstDecision.Item.Artist.Path, "Alien Ant Farm - 06 - Movies.mp3"),
                    Quality = firstDecision.Item.Quality,
                    FileTrackInfo = new ParsedTrackInfo()
                });

            Subject.Import(new List<ImportDecision<LocalTrack>> { firstDecision, secondDecision }, true);

            Mocker.GetMock<IMediaFileService>()
                .Verify(
                    service => service.GetFilesByAlbums(It.Is<List<int>>(ids =>
                        ids.Count == 1 &&
                        ids.Contains(firstDecision.Item.Album.Id))),
                    Times.Once());

            Mocker.GetMock<IMediaFileService>()
                .Verify(service => service.GetFilesByAlbum(It.IsAny<int>()), Times.Never());
        }

        [Test]
        public void should_delete_replaced_album_files_in_one_batch()
        {
            var previousFiles = Builder<TrackFile>.CreateListOfSize(2)
                .All()
                .With(file => file.Path = Path.Combine(_approvedDecisions.First().Item.Artist.Path, "previous.mp3"))
                .Build()
                .ToList();

            Mocker.GetMock<IMediaFileService>()
                .Setup(service => service.GetFilesByAlbums(It.IsAny<List<int>>()))
                .Returns(previousFiles);

            Mocker.GetMock<IDiskProvider>()
                .Setup(service => service.GetParentFolder(It.IsAny<string>()))
                .Returns<string>(path => Path.GetDirectoryName(path));

            Mocker.GetMock<IDiskProvider>()
                .Setup(service => service.FileExists(It.IsAny<string>()))
                .Returns(false);

            Subject.Import(new List<ImportDecision<LocalTrack>> { _approvedDecisions.First() }, true);

            Mocker.GetMock<IMediaFileService>()
                .Verify(
                    service => service.DeleteMany(
                        It.Is<List<TrackFile>>(files => files.Count > 0),
                        DeleteMediaFileReason.Upgrade),
                    Times.Once());

            Mocker.GetMock<IMediaFileService>()
                .Verify(service => service.Delete(It.IsAny<TrackFile>(), DeleteMediaFileReason.Upgrade), Times.Never());
        }

        [Test]
        public void should_lookup_existing_files_in_one_batch()
        {
            var first = _approvedDecisions[0].Item;
            var second = _approvedDecisions[1].Item;
            second.Path = Path.Combine(second.Artist.Path, "Alien Ant Farm - 02 - Movies.mp3");
            first.ExistingFile = true;
            second.ExistingFile = true;

            Subject.Import(_approvedDecisions.Take(2).ToList(), false);

            Mocker.GetMock<IMediaFileService>()
                .Verify(service => service.GetFileWithPath(It.Is<List<string>>(paths => paths.Count == 2)), Times.Once());
        }

        [Test]
        public void should_delete_existing_files_in_one_batch()
        {
            var first = _approvedDecisions[0].Item;
            var second = _approvedDecisions[1].Item;
            second.Path = Path.Combine(second.Artist.Path, "Alien Ant Farm - 02 - Movies.mp3");
            first.ExistingFile = true;
            second.ExistingFile = true;

            var previousFiles = new List<TrackFile>
            {
                Builder<TrackFile>.CreateNew().With(file => file.Path = first.Path).Build(),
                Builder<TrackFile>.CreateNew().With(file => file.Path = second.Path).Build()
            };

            Mocker.GetMock<IMediaFileService>()
                .Setup(service => service.GetFileWithPath(It.IsAny<List<string>>()))
                .Returns(previousFiles);

            Subject.Import(_approvedDecisions.Take(2).ToList(), false);

            Mocker.GetMock<IMediaFileService>()
                .Verify(
                    service => service.DeleteMany(
                        It.Is<List<TrackFile>>(files => files.Count == 2),
                        DeleteMediaFileReason.ManualOverride),
                    Times.Once());

            Mocker.GetMock<IMediaFileService>()
                .Verify(service => service.Delete(It.IsAny<TrackFile>(), DeleteMediaFileReason.ManualOverride), Times.Never());
        }

        [Test]
        public void should_lookup_download_history_once_for_all_files()
        {
            var downloadClientItem = new DownloadClientItem
            {
                DownloadId = "manual-import-download",
                DownloadClientInfo = _clientInfo
            };

            Mocker.GetMock<NzbDrone.Core.History.IHistoryService>()
                .Setup(service => service.FindByDownloadId(downloadClientItem.DownloadId))
                .Returns(new List<NzbDrone.Core.History.EntityHistory>());

            Subject.Import(_approvedDecisions, false, downloadClientItem);

            Mocker.GetMock<NzbDrone.Core.History.IHistoryService>()
                .Verify(service => service.FindByDownloadId(downloadClientItem.DownloadId), Times.Once());
        }

        [Test]
        public void should_publish_album_import_event_with_all_imported_tracks()
        {
            Subject.Import(_approvedDecisions, false);

            Mocker.GetMock<IEventAggregator>()
                .Verify(
                    v => v.PublishEvent(It.Is<AlbumImportedEvent>(e =>
                        e.ImportedTracks.Count == _approvedDecisions.Count &&
                        e.OldFiles.Count == 0)),
                    Times.Once());

            Mocker.GetMock<IAlbumService>()
                .Verify(service => service.GetAlbums(It.IsAny<IEnumerable<int>>()), Times.Never());
        }

        [Test]
        public void should_include_scene_name_with_new_downloads()
        {
            var firstDecision = _approvedDecisions.First();
            firstDecision.Item.SceneName = "Artist.Name.Album.Name.TrackNum.Track.Title.MP3256";

            Subject.Import(new List<ImportDecision<LocalTrack>> { _approvedDecisions.First() }, true);

            Mocker.GetMock<IUpgradeMediaFiles>()
                .Verify(v => v.UpgradeTrackFile(It.Is<TrackFile>(e => e.SceneName == firstDecision.Item.SceneName), _approvedDecisions.First().Item, false),
                    Times.Once());
        }
    }
}
