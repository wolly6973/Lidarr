import React, { useCallback, useMemo } from 'react';
import { useDispatch, useSelector } from 'react-redux';
import { useSelect } from 'App/SelectContext';
import ArtistAppState, { ArtistIndexAppState } from 'App/State/ArtistAppState';
import ClientSideCollectionAppState from 'App/State/ClientSideCollectionAppState';
import { REFRESH_ARTIST } from 'Commands/commandNames';
import PageToolbarButton from 'Components/Page/Toolbar/PageToolbarButton';
import { icons } from 'Helpers/Props';
import { executeCommand } from 'Store/Actions/commandActions';
import createArtistIndexPagedItemsSelector from 'Store/Selectors/createArtistIndexPagedItemsSelector';
import createCommandExecutingSelector from 'Store/Selectors/createCommandExecutingSelector';
import translate from 'Utilities/String/translate';
import getSelectedIds from 'Utilities/Table/getSelectedIds';

interface ArtistIndexRefreshArtistsButtonProps {
  isSelectMode: boolean;
  selectedFilterKey: string;
}

function ArtistIndexRefreshArtistsButton(
  props: ArtistIndexRefreshArtistsButtonProps
) {
  const isRefreshing = useSelector(
    createCommandExecutingSelector(REFRESH_ARTIST)
  );
  const {
    selectableItems,
    totalItems,
  }: ArtistAppState & ArtistIndexAppState & ClientSideCollectionAppState =
    useSelector(createArtistIndexPagedItemsSelector());

  const dispatch = useDispatch();
  const { isSelectMode, selectedFilterKey } = props;
  const [selectState] = useSelect();
  const { selectedState } = selectState;

  const selectedArtistIds = useMemo(() => {
    return getSelectedIds(selectedState);
  }, [selectedState]);

  const artistsToRefresh =
    isSelectMode && selectedArtistIds.length > 0
      ? selectedArtistIds
      : selectableItems.map((m) => m.id);

  let refreshLabel = translate('UpdateAll');

  if (selectedArtistIds.length > 0) {
    refreshLabel = translate('UpdateSelected');
  } else if (selectedFilterKey !== 'all') {
    refreshLabel = translate('UpdateFiltered');
  }

  const onPress = useCallback(() => {
    dispatch(
      executeCommand({
        name: REFRESH_ARTIST,
        artistIds: artistsToRefresh,
      })
    );
  }, [dispatch, artistsToRefresh]);

  return (
    <PageToolbarButton
      label={refreshLabel}
      isSpinning={isRefreshing}
      isDisabled={!totalItems}
      iconName={icons.REFRESH}
      onPress={onPress}
    />
  );
}

export default ArtistIndexRefreshArtistsButton;
