namespace RoundaboutDesigner.Models
{
    public enum SourceState { Pending, Running, Done, Empty }

    public class DataSourceStatus : System.ComponentModel.INotifyPropertyChanged
    {
        public string Criterion { get; set; }
        public string Source { get; set; }

        private SourceState _state = SourceState.Pending;
        public SourceState State
        {
            get => _state;
            set { _state = value; Raise(nameof(State)); Raise(nameof(StatusGlyph)); Raise(nameof(StatusText)); }
        }

        private int _count;
        public int Count
        {
            get => _count;
            set { _count = value; Raise(nameof(Count)); Raise(nameof(StatusText)); }
        }

        public string Display => $"{Criterion} ({Source})";

        public string StatusGlyph
        {
            get
            {
                switch (State)
                {
                    case SourceState.Running: return "…";
                    case SourceState.Done: return "✓";
                    case SourceState.Empty: return "—";
                    default: return "○"; // pending
                }
            }
        }

        public string StatusText
        {
            get
            {
                switch (State)
                {
                    case SourceState.Pending: return "waiting";
                    case SourceState.Running: return "fetching…";
                    case SourceState.Done: return $"{Count:N0}";
                    case SourceState.Empty: return "none";
                    default: return "";
                }
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        private void Raise(string n) =>
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));
    }

    public class ScoreBreakdownRow
        {
            public string Label { get; set; }        // "Safety"
            public double Contribution { get; set; }   // weighted, 0-100 scale
            public double BarWidth => Contribution * 2.0; // scale to px (tune factor)
        }
}