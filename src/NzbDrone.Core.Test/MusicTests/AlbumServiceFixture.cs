using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Music;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MusicTests.AlbumRepositoryTests
{
    [TestFixture]
    public class AlbumServiceFixture : CoreTest<AlbumService>
    {
        private List<Album> _albums;

        [SetUp]
        public void Setup()
        {
            _albums = new List<Album>();
            _albums.Add(new Album
            {
                Title = "ANThology",
                CleanTitle = "anthology",
            });

            _albums.Add(new Album
            {
                Title = "+",
                CleanTitle = "",
            });

            Mocker.GetMock<IAlbumRepository>()
                .Setup(s => s.GetAlbumsByArtistMetadataId(It.IsAny<int>()))
                .Returns(_albums);

            Mocker.SetConstant<IAlbumYearMatcher>(new AlbumYearMatcher());
        }

        private void GivenSimilarAlbum()
        {
            _albums.Add(new Album
            {
                Title = "ANThology2",
                CleanTitle = "anthology2",
            });
        }

        [Test]
        public void get_candidates_for_multiple_artists_should_score_each_artists_albums_independently()
        {
            var firstAlbum = new Album
            {
                Id = 101,
                ArtistMetadataId = 1,
                Title = "ANThology",
                CleanTitle = "anthology"
            };
            var secondAlbum = new Album
            {
                Id = 102,
                ArtistMetadataId = 2,
                Title = "ANThology",
                CleanTitle = "anthology"
            };

            Mocker.GetMock<IAlbumRepository>()
                .Setup(x => x.GetAlbumsByArtistMetadataIds(It.Is<IEnumerable<int>>(ids => ids.SequenceEqual(new[] { 1, 2 }))))
                .Returns(new List<Album> { firstAlbum, secondAlbum });

            var result = Subject.GetCandidates(new[] { 1, 2 }, "ANTholog");

            result.Select(x => x.Id).Should().Equal(101, 102);
            Mocker.GetMock<IAlbumRepository>()
                .Verify(x => x.GetAlbumsByArtistMetadataIds(It.IsAny<IEnumerable<int>>()), Times.Once);
            Mocker.GetMock<IAlbumRepository>()
                .Verify(x => x.GetAlbumsByArtistMetadataId(It.IsAny<int>()), Times.Never);
        }

        [TestCase("ANTholog", "ANThology")]
        [TestCase("antholoyg", "ANThology")]
        [TestCase("ANThology CD", "ANThology")]
        [TestCase("ANThology CD xxxx (Remastered) - [Oh please why do they do this?]", "ANThology")]
        [TestCase("+ (Plus) - I feel the need for redundant information in the title field", "+")]
        public void should_find_album_in_db_by_inexact_title(string title, string expected)
        {
            var album = Subject.FindByTitleInexact(0, title);

            album.Should().NotBeNull();
            album.Title.Should().Be(expected);
        }

        [TestCase("ANTholog")]
        [TestCase("antholoyg")]
        [TestCase("ANThology CD")]
        [TestCase("÷")]
        [TestCase("÷ (Divide)")]
        public void should_not_find_album_in_db_by_inexact_title_when_two_similar_matches(string title)
        {
            GivenSimilarAlbum();
            var album = Subject.FindByTitleInexact(0, title);

            album.Should().BeNull();
        }
    }
}
