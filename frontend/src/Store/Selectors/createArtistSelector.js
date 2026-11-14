import { createSelector } from 'reselect';

export function createArtistSelectorForHook(artistId) {
  return createSelector(
    (state) => state.artist.itemMap,
    (state) => state.artist.items,
    (state) => state.artistIndex.items,
    (itemMap, allArtists, pagedArtists) => {
      if (itemMap && artistId in itemMap) {
        return allArtists[itemMap[artistId]];
      }

      return pagedArtists.find((artist) => artist.id === artistId);
    }
  );
}

function createArtistSelector() {
  return createSelector(
    (state, { artistId }) => artistId,
    (state) => state.artist.itemMap,
    (state) => state.artist.items,
    (state) => state.artistIndex.items,
    (artistId, itemMap, allArtists, pagedArtists) => {
      if (artistId in itemMap) {
        return allArtists[itemMap[artistId]];
      }

      return pagedArtists.find((artist) => artist.id === artistId);
    }
  );
}

export default createArtistSelector;
