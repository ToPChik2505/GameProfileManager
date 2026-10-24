namespace GameProfileManager;

internal readonly record struct RouteViewState(double HorizontalOffset, double VerticalOffset,
    string? FocusName, string? FocusType, int FocusOrdinal);
