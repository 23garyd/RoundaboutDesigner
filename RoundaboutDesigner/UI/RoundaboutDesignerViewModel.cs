using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Input;
using ArcGIS.Core.Data.UtilityNetwork.Trace;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using RoundaboutDesigner.Config;
using RoundaboutDesigner.Gemini;
using RoundaboutDesigner.Models;
using RoundaboutDesigner.Services;
using RoundaboutDesigner.UI.Helpers;

namespace RoundaboutDesigner.UI
{
    internal class RoundaboutDesignerViewModel : DockPane
    {
        private const string _dockPaneID = "RoundaboutDesigner_DockPane";

        private readonly CityCatalogService _cityCatalog;
        private readonly GeminiService _geminiService;

        private readonly RagService _fhwaKb;
        private readonly RagService _cmfKb;

        private readonly AgentTools _agentTools = new AgentTools();
        private readonly List<Gemini.Content> _conversation = new List<Gemini.Content>();
        private const int MaxAgentSteps = 12;
        private string _lastLayerName;
        private List<IntersectionCandidate> _allScored = new List<IntersectionCandidate>();

        private ScoringWeights _lastWeights;
        public RoundaboutDesignerViewModel()
        {
            _cityCatalog = new CityCatalogService();
            _geminiService = new GeminiService();

            InitDataSourceTracker();

            _fhwaKb = new RagService(_geminiService);
            _cmfKb = new RagService(_geminiService);
            string kbDir = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "Knowledgebase");
            _fhwaKb.LoadKnowledgeBase(System.IO.Path.Combine(kbDir, "RoundaboutKB.json"));
            _cmfKb.LoadKnowledgeBase(System.IO.Path.Combine(kbDir, "CmfKB.json"));

            _agentTools.RunFullAnalysisAsync = async (state, city) => await RunPipelineAsync(state, city);
            _agentTools.FhwaKb = _fhwaKb;
            _agentTools.CmfKb = _cmfKb;

            _cityCatalog.Load();

            States = new ObservableCollection<string>(_cityCatalog.GetStates());
            Cities = new ObservableCollection<CityInfo>();
            ChatMessages = new ObservableCollection<ChatMessageViewModel>();
            AvailableModels = new ObservableCollection<GeminiModel>(
                (GeminiModel[])Enum.GetValues(typeof(GeminiModel)));

            // Initialize scoring weights from config
            var w = ScoringWeights.FromConfig();
            SafetyWeight = (int)(w.Safety * 100);
            TrafficWeight = (int)(w.Traffic * 100);
            FeasibilityWeight = (int)(w.Feasibility * 100);
            MultimodalWeight = (int)(w.Multimodal * 100);
            DestinationsWeight = (int)(w.Destinations * 100);

            MaxCandidates = 10;
            SelectedModel = GeminiModel.Flash3Preview;

            // Commands
            RunAnalysisCommand = new DelegateCommand(_ => RunAnalysis(), _ => CanRunAnalysis());
            SendChatCommand = new DelegateCommand(_ => SendChat(), _ => CanSendChat());

            AddSystemMessage("Pick a study area and click Find roundabout candidates to begin. Only sites scoring 50 or higher are returned.");
        }

        internal static void Show()
        {
            DockPane pane = FrameworkApplication.DockPaneManager.Find(_dockPaneID);
            pane?.Activate();
        }

        // --- Title ---
        private string _heading = "Roundabout Designer";
        public string Heading { get => _heading; set => SetProperty(ref _heading, value); }

        // --- Model selector ---
        public ObservableCollection<GeminiModel> AvailableModels { get; }
        
        private GeminiModel _selectedModel;
        public GeminiModel SelectedModel
        {
            get => _selectedModel;
            set => SetProperty(ref _selectedModel, value);
        }

        // --- Study area ---
        public ObservableCollection<DataSourceStatus> DataSources { get; }
            = new ObservableCollection<DataSourceStatus>();

        public ObservableCollection<ScoreBreakdownRow> TopBreakdown { get; }
            = new ObservableCollection<ScoreBreakdownRow>();

        private string _topCandidateName = "No results yet";
        public string TopCandidateName
        {
            get => _topCandidateName;
            set => SetProperty(ref _topCandidateName, value);
        }

        public bool HasDataSources => DataSources.Count > 0;
        public bool HasBreakdown => TopBreakdown.Count > 0;
        public bool HasCandidates => TopCandidates.Count > 0;

        private DataSourceStatus _dsTiger, _dsOsm, _dsHpms, _dsFars, _dsNsi, _dsAcs;

        private void InitDataSourceTracker()
        {
            DataSources.Clear();
            _dsTiger = new DataSourceStatus { Criterion = "Boundary", Source = "TIGER" };
            _dsOsm = new DataSourceStatus { Criterion = "Road network", Source = "OpenStreetMap" };
            _dsHpms = new DataSourceStatus { Criterion = "Traffic", Source = "HPMS AADT" };
            _dsFars = new DataSourceStatus { Criterion = "Safety", Source = "FARS crashes" };
            _dsNsi = new DataSourceStatus { Criterion = "Structures", Source = "NSI" };
            _dsAcs = new DataSourceStatus { Criterion = "Commute mode", Source = "ACS" };
            DataSources.Add(_dsTiger);
            DataSources.Add(_dsOsm);
            DataSources.Add(_dsHpms);
            DataSources.Add(_dsFars);
            DataSources.Add(_dsNsi);
            DataSources.Add(_dsAcs);
        }
        public ObservableCollection<CandidateListItem> TopCandidates { get; }
            = new ObservableCollection<CandidateListItem>();

        private CandidateListItem _selectedCandidateItem;
        public CandidateListItem SelectedCandidateItem
        {
            get => _selectedCandidateItem;
            set
            {
                if (SetProperty(ref _selectedCandidateItem, value) && value != null)
                    OnCandidateSelected(value.Candidate);
            }
        }

        public ObservableCollection<string> States { get; }
        public ObservableCollection<CityInfo> Cities { get; }

        private string _selectedState;
        public string SelectedState
        {
            get => _selectedState;
            set
            {
                if (SetProperty(ref _selectedState, value))
                {
                    RefreshCities();
                }
            }
        }

        private string _citySearchText = "";
        public string CitySearchText
        {
            get => _citySearchText;
            set
            {
                if (SetProperty(ref _citySearchText, value))
                {
                    RefreshCities();
                }
            }
        }

        private CityInfo _selectedCity;
        public CityInfo SelectedCity
        {
            get => _selectedCity;
            set
            {
                if (SetProperty(ref _selectedCity, value))
                {
                    NotifyPropertyChanged(nameof(SelectedAreaDisplay));
                    NotifyPropertyChanged(nameof(SelectedAreaSubtext));
                    NotifyPropertyChanged(nameof(SelectedAreaWarning));
                    NotifyPropertyChanged(nameof(HasSelectedArea));
                    if (value != null)
                        _ = ZoomToCityAsync(value);
                }
            }
        }

        public bool HasSelectedArea => _selectedCity != null;

        public string SelectedAreaDisplay =>
            _selectedCity == null ? "No study area selected" : _selectedCity.Name + ", " + _selectedCity.State;

        public string SelectedAreaSubtext =>
    _selectedCity == null
        ? "Pick a city to continue"
        : $"{_selectedCity.Population:N0} residents";

        public string SelectedAreaWarning
        {
            get
            {
                if (_selectedCity == null) return null;
                if (_selectedCity.Population > 2_000_000)
                    return "Analysis will be capped at 2,000 intersections — consider a borough or district instead";
                if (_selectedCity.Population > 500_000)
                    return "Large area — analysis may take 5–10 minutes";
                return null;
            }
        }
        public ICommand SendChatCommand { get; }
        public ICommand RunAnalysisCommand { get; }
        private bool CanSendChat() => !_isRunning && !string.IsNullOrWhiteSpace(UserChatInput);

        // --- Scoring weights (as 0-100 ints for slider bindings) ---
        private int _safetyWeight;
        public int SafetyWeight { get => _safetyWeight; set => SetProperty(ref _safetyWeight, value); }

        private int _trafficWeight;
        public int TrafficWeight { get => _trafficWeight; set => SetProperty(ref _trafficWeight, value); }

        private int _feasibilityWeight;
        public int FeasibilityWeight { get => _feasibilityWeight; set => SetProperty(ref _feasibilityWeight, value); }

        private int _multimodalWeight;
        public int MultimodalWeight { get => _multimodalWeight; set => SetProperty(ref _multimodalWeight, value); }

        private int _destinationsWeight;
        public int DestinationsWeight { get => _destinationsWeight; set => SetProperty(ref _destinationsWeight, value); }

        // --- Candidate count ---
        private int _maxCandidates;
        public int MaxCandidates
        {
            get => _maxCandidates;
            set
            {
                if (SetProperty(ref _maxCandidates, value))
                {
                    if (_allScored.Count > 0)
                        RebuildTopCandidates();
                }
            }
        }
        private void BuildBreakdownFor(IntersectionCandidate c)
        {
            var w = _lastWeights;
            if (w == null) return;

            TopCandidateName = $"#{c.Rank}  {c.IntersectionName} (score {c.FinalScore:F0})";
            TopBreakdown.Clear();
            TopBreakdown.Add(new ScoreBreakdownRow { Label = "Safety", Contribution = w.Safety * c.SafetyScore * 100 });
            TopBreakdown.Add(new ScoreBreakdownRow { Label = "Traffic", Contribution = w.Traffic * c.TrafficScore * 100 });
            TopBreakdown.Add(new ScoreBreakdownRow { Label = "Feasibility", Contribution = w.Feasibility * c.FeasibilityScore * 100 });
            TopBreakdown.Add(new ScoreBreakdownRow { Label = "Multimodal", Contribution = w.Multimodal * c.MultimodalScore * 100 });
            TopBreakdown.Add(new ScoreBreakdownRow { Label = "Destinations", Contribution = w.Destinations * c.DestinationsScore * 100 });

            NotifyPropertyChanged(nameof(HasBreakdown));   // <-- this is what was missing before
        }
        private async void OnCandidateSelected(IntersectionCandidate c)
        {
            if (c == null) return;

            // 1. Populate the score breakdown for this candidate.
            BuildBreakdownFor(c);

            // 2. Zoom the map to it.
            if (c.Location != null)
            {
                await ArcGIS.Desktop.Framework.Threading.Tasks.QueuedTask.Run(() =>
                {
                    var mv = ArcGIS.Desktop.Mapping.MapView.Active;
                    if (mv == null) return;
                    var buffer = ArcGIS.Core.Geometry.GeometryEngine.Instance
                        .GeodesicBuffer(c.Location, 250) as ArcGIS.Core.Geometry.Polygon;
                    mv.ZoomTo(buffer?.Extent ?? c.Location.Extent, System.TimeSpan.FromSeconds(1));
                });
            }
        }

        // --- Run state ---
        private bool _isRunning;
        public bool IsRunning
        {
            get => _isRunning;
            set
            {
                if (SetProperty(ref _isRunning, value))
                {
                    NotifyPropertyChanged(nameof(RunButtonLabel));
                }
            }
        }
        public string RunButtonLabel => _isRunning ? "Running analysis..." : "Find roundabout candidates";

        // --- Chat ---
        public ObservableCollection<ChatMessageViewModel> ChatMessages { get; }

        private string _userChatInput = "";
        public string UserChatInput
        {
            get => _userChatInput;
            set => SetProperty(ref _userChatInput, value);
        }

        // --- Commands ---

        // Auto-zoom the map to a city when it's selected in the list.
        private async Task ZoomToCityAsync(CityInfo city)
        {
            try
            {
                var tiger = new TigerService();
                var studyArea = await tiger.GetCityBoundaryAsync(city);
                await ArcGIS.Desktop.Framework.Threading.Tasks.QueuedTask.Run(() =>
                {
                    var mv = ArcGIS.Desktop.Mapping.MapView.Active;
                    if (mv == null || studyArea?.Boundary == null) return;
                    mv.ZoomTo(studyArea.Boundary.Extent, TimeSpan.FromSeconds(1.5));
                });
            }
            catch
            {
                // Silent: a failed zoom shouldn't disrupt selection. The boundary
                // is re-fetched at analysis time anyway.
            }
        }

        // --- Helpers ---
        private void RefreshCities()
        {
            Cities.Clear();
            if (string.IsNullOrEmpty(SelectedState)) return;
            var all = _cityCatalog.GetCities(SelectedState);
            var filtered = string.IsNullOrWhiteSpace(_citySearchText)
                ? all
                : all.Where(c => c.Name.IndexOf(_citySearchText, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            foreach (var c in filtered) Cities.Add(c);
        }

        // Finds a CityInfo by state + name. Used by both the Run button (via
        // _selectedCity's values) and the agent (via Gemini's string args).
        private CityInfo ResolveCity(string state, string city)
        {
            if (string.IsNullOrWhiteSpace(city)) return null;

            // Fast path: the UI selection already matches the request.
            if (_selectedCity != null &&
                string.Equals(_selectedCity.Name, city, StringComparison.OrdinalIgnoreCase))
                return _selectedCity;

            // Normalize a state abbreviation or full name to whatever your
            // catalog uses. Try the state as given first, then a loose match.
            var statesToSearch = string.IsNullOrWhiteSpace(state)
                ? _cityCatalog.GetStates()
                : new List<string> { state };

            foreach (var st in statesToSearch)
            {
                var matches = _cityCatalog.GetCities(st);
                var hit = matches?.FirstOrDefault(c =>
                    string.Equals(c.Name, city, StringComparison.OrdinalIgnoreCase));
                if (hit != null) return hit;
            }

            // Last resort: search all states for the city name alone.
            foreach (var st in _cityCatalog.GetStates())
            {
                var hit = _cityCatalog.GetCities(st)?
                    .FirstOrDefault(c => string.Equals(c.Name, city, StringComparison.OrdinalIgnoreCase));
                if (hit != null) return hit;
            }

            return null;
        }

        private void SetSource(DataSourceStatus row, SourceState state, int count = 0)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                row.State = state;
                if (state == SourceState.Done || state == SourceState.Empty) row.Count = count;
            });
        }
        private bool CanRunAnalysis() => _selectedCity != null && !_isRunning;

        private async void RunAnalysis()
        {
            await RunPipelineAsync(_selectedCity.State, _selectedCity.Name);
        }
        private void RebuildTopCandidates()
        {
            TopCandidates.Clear();
            foreach (var c in _allScored.Take(MaxCandidates))
                TopCandidates.Add(new CandidateListItem { Candidate = c });
            NotifyPropertyChanged(nameof(HasCandidates));
        }

        // Callable by both the Run button and the agent's RunFullAnalysis function.
        private async Task<string> RunPipelineAsync(string state, string city)
        {
            IsRunning = true;
            try
            {
                InitDataSourceTracker();
                // Resolve a CityInfo from the passed-in strings (agent path) or
                // fall back to the UI selection (button path).
                CityInfo cityInfo = ResolveCity(state, city);
                if (cityInfo == null)
                    return $"ERROR: could not find '{city}, {state}' in the city catalog. " +
                           "It may not be in the seed list (currently NH/TX/CA/NY).";

                AddSystemMessage($"Setting study area to {cityInfo.Name}, {cityInfo.State}");


                var tiger = new TigerService();
                SetSource(_dsTiger, SourceState.Running);
                var studyArea = await tiger.GetCityBoundaryAsync(cityInfo);
                SetSource(_dsTiger, SourceState.Done, studyArea.Boundary.PointCount);
                AddSystemMessage($"Boundary retrieved from TIGER ({studyArea.Boundary.PointCount} points)");

                AddSystemMessage("Fetching road network from OpenStreetMap (this may take a moment)...");
                var osm = new OsmService();
                SetSource(_dsOsm, SourceState.Running);
                var network = await osm.FetchRoadNetworkAsync(studyArea.Boundary);
                SetSource(_dsOsm, SourceState.Done, network.Ways.Count);
                AddSystemMessage($"Loaded {network.Ways.Count:N0} road segments and {network.Nodes.Count:N0} nodes");

                AddSystemMessage("Identifying intersection candidates...");
                var extractor = new IntersectionExtractor();
                var candidates = await extractor.ExtractAsync(network);
                AddSystemMessage($"Found {candidates.Count:N0} intersection candidates (3+ legs, not already roundabouts)");

                AddSystemMessage("Fetching traffic volumes from HPMS...");
                var hpms = new HpmsService();
                SetSource(_dsHpms, SourceState.Running);
                int segCount = await hpms.AttachAadtAsync(candidates, studyArea.Boundary);
                int withAadt = candidates.Count(c => c.MaxLegAadt > 0);
                SetSource(_dsHpms, withAadt > 0 ? SourceState.Done : SourceState.Empty, withAadt);
                AddSystemMessage($"Retrieved {segCount:N0} HPMS segments; matched AADT to {withAadt:N0} of {candidates.Count:N0} candidates");

                AddSystemMessage("Resolving counties for crash data...");
                var counties = await tiger.GetCountiesAsync(studyArea.Boundary);
                AddSystemMessage($"Study area spans {counties.Count} county/counties");

                AddSystemMessage("Fetching fatal crashes from NHTSA FARS...");
                var fars = new FarsService();
                SetSource(_dsFars, SourceState.Running);
                var farsResult = await fars.AttachFatalCrashesAsync(candidates, counties);
                int withCrashes = candidates.Count(c => c.FatalCrashes5Yr > 0);
                SetSource(_dsFars, withCrashes > 0 ? SourceState.Done : SourceState.Empty, withCrashes);
                foreach (var diag in farsResult.Diagnostics)
                    AddSystemMessage(diag);
                AddSystemMessage($"Total: {farsResult.CrashesRetrieved:N0} geocoded fatal crashes; " +
                                 $"{farsResult.CrashesMatched:N0} candidates had one within 50m");

                // --- Phase 6d: NSI structures (feasibility + destinations) ---
                AddSystemMessage("Fetching structures from USACE NSI...");
                var nsi = new NsiService(AppConfig.Instance.Endpoints.Nsi);
                SetSource(_dsNsi, SourceState.Running);
                var structures = await nsi.FetchStructuresAsync(studyArea.Boundary);

                await QueuedTask.Run(() =>
                {
                    nsi.AttachStructureAttrs(
                        candidates,
                        structures,
                        AppConfig.Instance.ScoringDefaults.NsiFootprintBufferM,
                        AppConfig.Instance.ScoringDefaults.DestinationRadiusM);
                });
                int withStructures = candidates.Count(c => c.NearbyStructureValue > 0);
                SetSource(_dsNsi, structures.Count > 0 ? SourceState.Done : SourceState.Empty, structures.Count);
                AddSystemMessage($"Retrieved {structures.Count:N0} structures from NSI");

                AddSystemMessage($"Attached structure attributes; {withStructures:N0} candidates have structures within footprint");

                // --- Phase 6d: ACS commute mode (multimodal baseline) ---
                AddSystemMessage("Fetching commute mode share from Census ACS...");
                // State + county FIPS both come from the resolved county tuples.
                string stateFips = counties.Select(c => c.stateFp).FirstOrDefault();
                var countyFips = counties.Select(c => c.countyFp).ToList();

                var acs = new CensusAcsService(
                    AppConfig.Instance.Endpoints.CensusAcs,
                    AppConfig.Instance.CensusApiKey);
                SetSource(_dsAcs, SourceState.Running);
                var (walkBike, transit) = await acs.GetCommuteSharesAsync(stateFips, countyFips);
                SetSource(_dsAcs, walkBike > 0 || transit > 0 ? SourceState.Done : SourceState.Empty,
                          (int)System.Math.Round((walkBike + transit) * 100));
                foreach (var c in candidates)
                {
                    c.WalkBikeCommuteShare = walkBike;
                    c.TransitCommuteShare = transit;
                }
                AddSystemMessage($"Commute baseline: {walkBike:P1} walk/bike, {transit:P1} transit (city-wide)");

                // --- Phase 7: score, recommend type, estimate cost, write layer ---
                AddSystemMessage("Scoring candidates...");
                var weights = new ScoringWeights
                {
                    Safety = SafetyWeight / 100.0,
                    Traffic = TrafficWeight / 100.0,
                    Feasibility = FeasibilityWeight / 100.0,
                    Multimodal = MultimodalWeight / 100.0,
                    Destinations = DestinationsWeight / 100.0
                };

                var scorer = new ScoringService();
                scorer.ScoreAll(candidates, weights);

                var typer = new TypeRecommendationService();
                typer.RecommendAll(candidates);

                var coster = new CostEstimationService();
                coster.EstimateAll(candidates);

                double threshold = AppConfig.Instance.ScoringDefaults.MinScoreThreshold;
                int passing = candidates.Count(c => c.FinalScore >= threshold);
                AddSystemMessage($"Scored {candidates.Count:N0} candidates; " +
                                 $"{passing:N0} cleared the quality threshold of {threshold:N0}.");

                _lastWeights = weights;  // remember what we scored with

                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    _allScored = candidates.OrderBy(c => c.Rank).ToList();
                    TopCandidates.Clear();
                    var topN = candidates.OrderBy(c => c.Rank).Take(MaxCandidates);
                    foreach (var c in topN)
                        TopCandidates.Add(new CandidateListItem { Candidate = c });

                    NotifyPropertyChanged(nameof(HasCandidates));

                    RebuildTopCandidates();
                    // Auto-select the top candidate so the breakdown shows immediately.
                    if (TopCandidates.Count > 0)
                        SelectedCandidateItem = TopCandidates[0];
                });

                AddSystemMessage("Writing candidate feature class to the default geodatabase...");
                var writer = new CandidateLayerWriter();
                string fcPath = await writer.WriteAsync(candidates, studyArea, threshold);
                AddSystemMessage($"Wrote feature class and added it to the map: {fcPath}");

                var result = new AnalysisResult
                {
                    StudyArea = studyArea,
                    WeightsUsed = weights,
                    RequestedCount = MaxCandidates,
                    TotalIntersectionsExamined = candidates.Count,
                    TotalPassingThreshold = passing,
                    ThresholdUsed = threshold,
                    OutputFeatureClassPath = fcPath,
                    Candidates = candidates
                        .OrderBy(c => c.Rank)
                        .Take(MaxCandidates)
                        .ToList()
                };

                var best = result.Candidates.FirstOrDefault();
                if (best != null)
                {
                    AddAssistantMessage(
                        $"Done. Top candidate in {studyArea.CityName}: {best.IntersectionName} " +
                        $"(score {best.FinalScore:F0}, {best.ConfidenceLevel.ToLower()} confidence). " +
                        $"Recommended {best.Recommendation.TypeLabel.ToLower()}, " +
                        $"est. {best.Cost.TotalFormatted}. {best.Recommendation.Justification} " +
                        (result.ResultsTruncatedByThreshold
                            ? $"Only {passing} of {MaxCandidates} requested cleared the threshold."
                            : "") +
                        " All candidates were written to the geodatabase layer for review.");
                }
                else
                {
                    AddAssistantMessage("Scoring completed but produced no candidates to rank.");
                }

                // After scoring + writing the layer, capture state for the agent:
                _agentTools.AllCandidates = candidates;
                _agentTools.CurrentView = candidates.OrderBy(c => c.Rank).ToList();
                _agentTools.Weights = weights;
                _agentTools.StudyArea = studyArea;
                _agentTools.Threshold = threshold;
                _agentTools.LayerName = System.IO.Path.GetFileName(fcPath);
                return $"Analysis complete for {studyArea.CityName}: {candidates.Count} candidates, " +
                       $"{passing} cleared the threshold. Top: rank 1 {result.Candidates.FirstOrDefault()?.IntersectionName}.";
            }
            catch (Exception ex)
            {
                AddSystemMessage($"Error: {ex.Message}");
                return $"ERROR: {ex.Message}";
            }
            finally
            {
                IsRunning = false;
            }
        }

        private async void SendChat()
        {
            string userText = UserChatInput?.Trim();
            if (string.IsNullOrWhiteSpace(userText)) return;

            AddUserMessage(userText);
            UserChatInput = "";

            _conversation.Add(new Gemini.Content
            {
                Role = "user",
                Parts = new List<Gemini.GeminiPart> { new Gemini.GeminiPart { Text = BuildTurnPrompt(userText) } }
            });

            try
            {
                int steps = 0;
                int pipelineRunsThisTurn = 0;
                bool working = true;

                while (working)
                {
                    if (++steps > MaxAgentSteps)
                    {
                        AddSystemMessage("Agent reached the step limit and stopped.");
                        break;
                    }

                    var part = await _geminiService.SendMessageAsync(_conversation, SelectedModel);

                    if (part?.FunctionCall != null)
                    {
                        string fn = part.FunctionCall.Name;

                        // Guard: at most one full pipeline run per user message.
                        if (fn == "RunFullAnalysis" && ++pipelineRunsThisTurn > 1)
                        {
                            AddSystemMessage("(Skipped a second full analysis in one turn.)");
                            break;
                        }

                        // Log the model's intent turn.
                        _conversation.Add(new Gemini.Content
                        {
                            Role = "model",
                            Parts = new List<Gemini.GeminiPart> { part }
                        });

                        AddSystemMessage($"→ {fn}");
                        string result = await _agentTools.ExecuteAsync(fn, part.FunctionCall.Args);

                        // Send the result back as a functionResponse on a user turn
                        // (must immediately follow the model functionCall turn).
                        _conversation.Add(new Gemini.Content
                        {
                            Role = "user",
                            Parts = new List<Gemini.GeminiPart>
                            {
                                new Gemini.GeminiPart
                                {
                                    FunctionResponse = new Gemini.FunctionResponse
                                    {
                                        Id = part.FunctionCall.Id,
                                        Name = fn,
                                        Response = new { result = result }
                                    }
                                }
                            }
                        });
                        // loop continues; Gemini sees the result and decides next step
                    }
                    else if (!string.IsNullOrEmpty(part?.Text))
                    {
                        AddAssistantMessage(part.Text.Trim());
                        _conversation.Add(new Gemini.Content
                        {
                            Role = "model",
                            Parts = new List<Gemini.GeminiPart> { part }
                        });
                        working = false;
                    }
                    else
                    {
                        working = false; // malformed/empty
                    }
                }
            }
            catch (Exception ex)
            {
                AddSystemMessage($"Agent error: {ex.Message}");
            }
        }

        // Prepend the system instructions to the FIRST user message of the conversation.
        private string BuildTurnPrompt(string userText)
        {
            if (_conversation.Count > 0) return userText; // only inject once, on turn 1

            return
                "You are a transportation planning assistant embedded in ArcGIS Pro. You help a city " +
                "planner screen intersections for roundabout conversion. You have functions to run a full " +
                "analysis for a city, and to filter, sort, re-weight, summarize, explain, and zoom to the " +
                "resulting candidates. Prefer calling GetCurrentState first if you're unsure what's loaded. " +
                "Chain functions to satisfy multi-part requests. Keep final answers concise and planner-friendly. " +
                "Costs are planning-level estimates; scores are relative within the chosen city, not absolute.\n\n" +
                "When justifying a roundabout type, call ExplainRecommendation and cite the FHWA " +
                "Roundabouts Informational Guide. When stating a safety benefit, call EstimateBenefit " +
                "and cite the specific CMF or CRF percentage with its star rating. Never invent crash " +
                "statistics or design thresholds — only use values returned by these functions. If a " +
                "function returns nothing relevant, say so rather than guessing.\n\n" +
                "USER: " + userText;
        }

        //private async void RunAnalysis()
        //{
        //    IsRunning = true;
        //    ChatMessages.Clear();

        //    try
        //    {
        //        AddSystemMessage($"Setting study area to {_selectedCity.Name}, {_selectedCity.State}");

        //        var tiger = new TigerService();
        //        var studyArea = await tiger.GetCityBoundaryAsync(_selectedCity);
        //        AddSystemMessage($"Boundary retrieved from TIGER ({studyArea.Boundary.PointCount} points)");

        //        AddSystemMessage("Fetching road network from OpenStreetMap (this may take a moment)...");
        //        var osm = new OsmService();
        //        var network = await osm.FetchRoadNetworkAsync(studyArea.Boundary);
        //        AddSystemMessage($"Loaded {network.Ways.Count:N0} road segments and {network.Nodes.Count:N0} nodes");

        //        AddSystemMessage("Identifying intersection candidates...");
        //        var extractor = new IntersectionExtractor();
        //        var candidates = await extractor.ExtractAsync(network);
        //        AddSystemMessage($"Found {candidates.Count:N0} intersection candidates (3+ legs, not already roundabouts)");

        //        AddSystemMessage("Fetching traffic volumes from HPMS...");
        //        var hpms = new HpmsService();
        //        int segCount = await hpms.AttachAadtAsync(candidates, studyArea.Boundary);
        //        int withAadt = candidates.Count(c => c.MaxLegAadt > 0);
        //        AddSystemMessage($"Retrieved {segCount:N0} HPMS segments; matched AADT to {withAadt:N0} of {candidates.Count:N0} candidates");

        //        AddSystemMessage("Resolving counties for crash data...");
        //        var counties = await tiger.GetCountiesAsync(studyArea.Boundary);
        //        AddSystemMessage($"Study area spans {counties.Count} county/counties");

        //        AddSystemMessage("Fetching fatal crashes from NHTSA FARS...");
        //        var fars = new FarsService();
        //        var farsResult = await fars.AttachFatalCrashesAsync(candidates, counties);
        //        foreach (var diag in farsResult.Diagnostics)
        //            AddSystemMessage(diag);
        //        AddSystemMessage($"Total: {farsResult.CrashesRetrieved:N0} geocoded fatal crashes; " +
        //                         $"{farsResult.CrashesMatched:N0} candidates had one within 50m");

        //        // --- Phase 6d: NSI structures (feasibility + destinations) ---
        //        AddSystemMessage("Fetching structures from USACE NSI...");
        //        var nsi = new NsiService(AppConfig.Instance.Endpoints.Nsi);
        //        var structures = await nsi.FetchStructuresAsync(studyArea.Boundary);
        //        AddSystemMessage($"Retrieved {structures.Count:N0} structures from NSI");

        //        await QueuedTask.Run(() =>
        //        {
        //            nsi.AttachStructureAttrs(
        //                candidates,
        //                structures,
        //                AppConfig.Instance.ScoringDefaults.NsiFootprintBufferM,
        //                AppConfig.Instance.ScoringDefaults.DestinationRadiusM);
        //        });
        //        int withStructures = candidates.Count(c => c.NearbyStructureValue > 0);
        //        AddSystemMessage($"Attached structure attributes; {withStructures:N0} candidates have structures within footprint");

        //        // --- Phase 6d: ACS commute mode (multimodal baseline) ---
        //        AddSystemMessage("Fetching commute mode share from Census ACS...");
        //        // State + county FIPS both come from the resolved county tuples.
        //        string stateFips = counties.Select(c => c.stateFp).FirstOrDefault();
        //        var countyFips = counties.Select(c => c.countyFp).ToList();

        //        var acs = new CensusAcsService(
        //            AppConfig.Instance.Endpoints.CensusAcs,
        //            AppConfig.Instance.CensusApiKey);
        //        var (walkBike, transit) = await acs.GetCommuteSharesAsync(stateFips, countyFips);
        //        foreach (var c in candidates)
        //        {
        //            c.WalkBikeCommuteShare = walkBike;
        //            c.TransitCommuteShare = transit;
        //        }
        //        AddSystemMessage($"Commute baseline: {walkBike:P1} walk/bike, {transit:P1} transit (city-wide)");

        //        // --- Phase 7: score, recommend type, estimate cost, write layer ---
        //        AddSystemMessage("Scoring candidates...");
        //        var weights = new ScoringWeights
        //        {
        //            Safety = SafetyWeight / 100.0,
        //            Traffic = TrafficWeight / 100.0,
        //            Feasibility = FeasibilityWeight / 100.0,
        //            Multimodal = MultimodalWeight / 100.0,
        //            Destinations = DestinationsWeight / 100.0
        //        };

        //        var scorer = new ScoringService();
        //        scorer.ScoreAll(candidates, weights);

        //        var typer = new TypeRecommendationService();
        //        typer.RecommendAll(candidates);

        //        var coster = new CostEstimationService();
        //        coster.EstimateAll(candidates);

        //        double threshold = AppConfig.Instance.ScoringDefaults.MinScoreThreshold;
        //        int passing = candidates.Count(c => c.FinalScore >= threshold);
        //        AddSystemMessage($"Scored {candidates.Count:N0} candidates; " +
        //                         $"{passing:N0} cleared the quality threshold of {threshold:N0}.");

        //        AddSystemMessage("Writing candidate feature class to the default geodatabase...");
        //        var writer = new CandidateLayerWriter();
        //        string fcPath = await writer.WriteAsync(candidates, studyArea, threshold);
        //        AddSystemMessage($"Wrote feature class and added it to the map: {fcPath}");

        //        var result = new AnalysisResult
        //        {
        //            StudyArea = studyArea,
        //            WeightsUsed = weights,
        //            RequestedCount = MaxCandidates,
        //            TotalIntersectionsExamined = candidates.Count,
        //            TotalPassingThreshold = passing,
        //            ThresholdUsed = threshold,
        //            OutputFeatureClassPath = fcPath,
        //            Candidates = candidates
        //                .OrderBy(c => c.Rank)
        //                .Take(MaxCandidates)
        //                .ToList()
        //        };

        //        var top = result.Candidates.FirstOrDefault();
        //        if (top != null)
        //        {
        //            AddAssistantMessage(
        //                $"Done. Top candidate in {studyArea.CityName}: {top.IntersectionName} " +
        //                $"(score {top.FinalScore:F0}, {top.ConfidenceLevel.ToLower()} confidence). " +
        //                $"Recommended {top.Recommendation.TypeLabel.ToLower()}, " +
        //                $"est. {top.Cost.TotalFormatted}. {top.Recommendation.Justification} " +
        //                (result.ResultsTruncatedByThreshold
        //                    ? $"Only {passing} of {MaxCandidates} requested cleared the threshold."
        //                    : "") +
        //                " All candidates were written to the geodatabase layer for review.");
        //        }
        //        else
        //        {
        //            AddAssistantMessage("Scoring completed but produced no candidates to rank.");
        //        }
        //    }
        //    catch (System.Exception ex)
        //    {
        //        AddSystemMessage($"Error: {ex.Message}");
        //    }
        //    finally
        //    {
        //        IsRunning = false;
        //    }
        //}

        private void AddSystemMessage(string text)
        {
            ChatMessages.Add(new ChatMessageViewModel { Role = ChatRole.System, Text = text });
        }

        private void AddAssistantMessage(string text)
        {
            ChatMessages.Add(new ChatMessageViewModel { Role = ChatRole.Assistant, Text = text });
        }

        private void AddUserMessage(string text)
        {
            ChatMessages.Add(new ChatMessageViewModel { Role = ChatRole.User, Text = text });
        }
    }

    internal class RoundaboutDesigner_ShowButton : Button
    {
        protected override void OnClick()
        {
            RoundaboutDesignerViewModel.Show();
        }
    }

    public enum ChatRole { User, Assistant, System }

    public class ChatMessageViewModel
    {
        public ChatRole Role { get; set; }
        public string Text { get; set; }
        public string Prefix
        {
            get
            {
                switch (Role)
                {
                    case ChatRole.User: return "You:";
                    case ChatRole.Assistant: return "Assistant:";
                    default: return "";
                }
            }
        }
    }
}