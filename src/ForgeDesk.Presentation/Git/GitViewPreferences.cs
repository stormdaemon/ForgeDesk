namespace ForgeDesk.Presentation.Git;

/// <summary>
/// View choices of the Git tab remembered for the session and shared by every open project: the
/// last sub-view, whether the commit description is expanded, whether History walks all branches.
/// </summary>
public sealed class GitViewPreferences
{
    private readonly Lock _gate = new();
    private GitView _lastView = GitView.Changes;
    private bool _descriptionExpanded;
    private bool _historyAllBranches;

    public GitView LastView
    {
        get
        {
            lock (_gate)
            {
                return _lastView;
            }
        }

        set
        {
            lock (_gate)
            {
                _lastView = value;
            }
        }
    }

    public bool CommitDescriptionExpanded
    {
        get
        {
            lock (_gate)
            {
                return _descriptionExpanded;
            }
        }

        set
        {
            lock (_gate)
            {
                _descriptionExpanded = value;
            }
        }
    }

    public bool HistoryAllBranches
    {
        get
        {
            lock (_gate)
            {
                return _historyAllBranches;
            }
        }

        set
        {
            lock (_gate)
            {
                _historyAllBranches = value;
            }
        }
    }
}
