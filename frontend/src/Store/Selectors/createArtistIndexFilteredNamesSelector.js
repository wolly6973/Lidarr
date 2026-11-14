import { createSelector } from 'reselect';
import createClientSideCollectionSelector from './createClientSideCollectionSelector';

function createArtistIndexFilteredNamesSelector() {
  return createSelector(
    createClientSideCollectionSelector('artist', 'artistIndex', true),
    (artist) => artist.items.map(({ id, sortName }) => ({ id, sortName }))
  );
}

export default createArtistIndexFilteredNamesSelector;
