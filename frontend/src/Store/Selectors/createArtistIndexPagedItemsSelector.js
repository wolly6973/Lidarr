import { createSelector } from 'reselect';

function createArtistIndexPagedItemsSelector() {
  return createSelector(
    (state) => state.artistIndex,
    (state) => state.customFilters.items,
    (state) => state.artist.items.length,
    (artistIndex, allCustomFilters, totalItems) => {
      const customFilters = allCustomFilters.filter((customFilter) => {
        return customFilter.type === 'artistIndex' || customFilter.type === 'artist';
      });

      return {
        ...artistIndex,
        customFilters,
        totalItems,
        selectableItems: (artistIndex.matchingIds || []).map((id) => ({ id })),
        items: (artistIndex.items || []).map((artist) => ({
          id: artist.id,
          sortName: artist.sortName
        }))
      };
    }
  );
}

export default createArtistIndexPagedItemsSelector;
