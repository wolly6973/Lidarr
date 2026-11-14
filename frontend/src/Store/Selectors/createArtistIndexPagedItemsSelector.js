import { createSelector } from 'reselect';

function createArtistIndexPagedItemsSelector() {
  return createSelector(
    (state) => state.artistIndex,
    (state) => state.customFilters.items,
    (artistIndex, allCustomFilters) => {
      const customFilters = allCustomFilters.filter((customFilter) => {
        return customFilter.type === 'artistIndex' || customFilter.type === 'artist';
      });

      return {
        ...artistIndex,
        customFilters,
        totalItems: artistIndex.totalRecords || 0,
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
