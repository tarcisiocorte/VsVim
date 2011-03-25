﻿namespace Vim

open Microsoft.VisualStudio.Text
open Microsoft.VisualStudio.Text.Operations
open Microsoft.VisualStudio.Text.Editor
open Microsoft.VisualStudio.Text.Outlining
open NullableUtil

type IncrementalSearchData = {
    /// The point from which the search needs to occur 
    StartPoint : ITrackingPoint;

    /// Most recent result of the search
    SearchResult : SearchResult

} with 

    member x.SearchData = x.SearchResult.SearchData

type internal IncrementalSearch
    (
        _operations : Modes.ICommonOperations,
        _settings : IVimLocalSettings,
        _navigator : ITextStructureNavigator,
        _search : ISearchService,
        _statusUtil : IStatusUtil,
        _vimData : IVimData
    ) =

    let _globalSettings = _settings.GlobalSettings
    let _textView = _operations.TextView
    let mutable _data : IncrementalSearchData option = None
    let _searchOptions = SearchOptions.ConsiderIgnoreCase ||| SearchOptions.ConsiderSmartCase
    let _currentSearchUpdated = Event<SearchResult>()
    let _currentSearchCompleted = Event<SearchResult>()
    let _currentSearchCancelled = Event<SearchData>()

    /// There is a big gap between the behavior and documentation of key mapping for an 
    /// incremental search operation.  The documentation properly documents the language
    /// mapping in "help language-mapping" and 'help imsearch'.  But it doesn't document
    /// that command mapping should used when 'imsearch' doesn't apply although it's
    /// the practice in implementation
    ///
    /// TODO: actually implement the 'imsearch' option and fix this
    member x.RemapMode = KeyRemapMode.Command

    /// Add the pattern to the incremental search history
    member x.AddToHistory (searchData : SearchData) = 
        let pattern = searchData.Text.RawText
        _vimData.IncrementalSearchHistory.Add pattern

    member x.Begin kind = 
        let caret = TextViewUtil.GetCaretPoint _textView
        let start = Util.GetSearchPoint kind caret
        let searchData = {Text = SearchText.Pattern(StringUtil.empty); Kind = kind; Options = _searchOptions}
        let data = {
            StartPoint = start.Snapshot.CreateTrackingPoint(start.Position, PointTrackingMode.Negative)
            SearchResult = SearchResult.NotFound searchData
        }

        _data <- Some data

        // Raise the event
        _currentSearchUpdated.Trigger data.SearchResult

        let historyClient = { 
            new IHistoryClient<IncrementalSearchData, SearchResult> with
                member this.HistoryList = _vimData.IncrementalSearchHistory
                member this.RemapMode = Some x.RemapMode
                member this.Beep() = _operations.Beep()
                member this.ProcessCommand data command = x.RunSearch data command
                member this.Completed (data : IncrementalSearchData) _ = x.Completed data
                member this.Cancelled (data : IncrementalSearchData) = x.Cancelled data.SearchData
            }
        
        let storage = HistoryUtil.Begin historyClient data StringUtil.empty
        storage.CreateBindData ()

    // Reset the view to it's original state.  We should only be doing this if the
    // 'incsearch' option is set.  Otherwise the view shouldn't change during an 
    // incremental search
    member x.ResetView () =
        if _globalSettings.IncrementalSearch then
             _operations.EnsureCaretOnScreenAndTextExpanded()

    /// Run the search for the specified text.  Returns the new IncrementalSearchData resulting
    /// from the search
    member x.RunSearch (data : IncrementalSearchData) pattern =
        let searchData = { data.SearchData with Text = SearchText.Pattern pattern }

        // Get the SearchResult value for the new text
        let searchResult =
            if StringUtil.isNullOrEmpty pattern then
                SearchResult.NotFound searchData
            else
                match TrackingPointUtil.GetPoint _textView.TextSnapshot data.StartPoint with
                | None ->
                    SearchResult.NotFound searchData
                | Some point ->
                    let options = SearchOptions.ConsiderIgnoreCase ||| SearchOptions.ConsiderIgnoreCase
                    _search.FindNext searchData point _navigator 

        // Update our state based on the SearchResult.  Only update the view if the 'incsearch'
        // option is set
        if _globalSettings.IncrementalSearch then
            match searchResult with
            | SearchResult.Found (_, span, _) -> _operations.EnsurePointOnScreenAndTextExpanded span.Start
            | SearchResult.NotFound _ -> x.ResetView ()

        _currentSearchUpdated.Trigger searchResult
        let data = { data with SearchResult = searchResult }
        _data <- Some data
        data

    /// Called when the processing is completed.  Raise the completed event and return
    /// the final SearchResult
    member x.Completed (data : IncrementalSearchData) =
        let data =
            if StringUtil.isNullOrEmpty data.SearchData.Text.RawText then
                // When the user simply hits Enter on an empty incremental search then
                // we should be re-using the 'LastSearch' value.
                x.RunSearch data _vimData.LastSearchData.Text.RawText
            else 
                data

        // Need to update the status if the search wrapped around
        match data.SearchResult with
        | SearchResult.Found (_, _, didWrap) ->
            if didWrap then
                let message = 
                    if data.SearchData.Kind.IsAnyForward then Resources.Common_SearchForwardWrapped
                    else Resources.Common_SearchBackwardWrapped
                _statusUtil.OnWarning message
        | SearchResult.NotFound _ ->
            ()

        _vimData.LastSearchData <- data.SearchData
        _currentSearchCompleted.Trigger data.SearchResult
        _data <- None
        data.SearchResult

    /// Cancel the search.  Provide the last value searched for
    member x.Cancelled data =
        x.ResetView ()
        _currentSearchCancelled.Trigger data
        _data <- None

    interface IIncrementalSearch with
        member x.InSearch = Option.isSome _data
        member x.SearchService = _search
        member x.WordNavigator = _navigator
        member x.CurrentSearch = 
            match _data with 
            | Some(data) -> Some data.SearchData
            | None -> None
        member x.Begin kind = x.Begin kind
        [<CLIEvent>]
        member x.CurrentSearchUpdated = _currentSearchUpdated.Publish
        [<CLIEvent>]
        member x.CurrentSearchCompleted = _currentSearchCompleted.Publish 
        [<CLIEvent>]
        member x.CurrentSearchCancelled = _currentSearchCancelled.Publish


