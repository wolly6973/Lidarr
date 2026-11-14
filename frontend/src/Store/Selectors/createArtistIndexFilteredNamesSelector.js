import { createSelector } from 'reselect';

function createArtistIndexFilteredNamesSelector() {
  return createSelector(
    (state) => state.artistIndex.matchingArtists,
    (matchingArtists) => matchingArtists || []
  );
}

export default createArtistIndexFilteredNamesSelector;
