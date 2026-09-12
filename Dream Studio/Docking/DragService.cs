using System;
using System.Linq;
using Avalonia;

namespace Dream.Studio.Docking
{
    /// <summary>
    /// Coordinates a single in-flight panel drag across all hosts of a <see cref="DockWorkspace"/>.
    /// The service owns drag activation (a small move threshold distinguishes a click from a drag),
    /// the drag ghost, the drop overlay shown on the host under the cursor, and the final commit:
    /// detach from the source manager then either dock into the resolved target or float a new window.
    /// </summary>
    internal sealed class DragService
    {
        private const double ActivateThreshold = 5;

        private readonly DockWorkspace _workspace;

        private DockManager? _sourceManager;
        private DockPanel? _panel;
        private DockHost? _sourceHost;
        private PixelPoint _startScreen;
        private bool _active;
        private DropTarget _current = DropTarget.Empty;

        public bool IsDragging => _panel is not null;

        public DragService(DockWorkspace workspace) => _workspace = workspace;

        public void Begin(DockManager manager, DockPanel panel, DockHost host, PixelPoint screen)
        {
            _sourceManager = manager;
            _panel = panel;
            _sourceHost = host;
            _startScreen = screen;
            _active = false;
            _current = DropTarget.Empty;
        }

        public void Move(PixelPoint screen)
        {
            if (_panel is null || _sourceHost is null)
            {
                return;
            }

            if (!_active)
            {
                var dx = screen.X - _startScreen.X;
                var dy = screen.Y - _startScreen.Y;
                if ((dx * dx) + (dy * dy) < ActivateThreshold * ActivateThreshold)
                {
                    return;
                }

                _active = true;
                _sourceHost.ShowGhost(_panel, screen);
            }

            _sourceHost.MoveGhost(screen);

            var host = _workspace.HitTestHost(screen);
            if (host is null)
            {
                _current = DropTarget.Empty;
                HideAllHints();
                return;
            }

            _current = host.ResolveTarget(screen);
            if (_current.IsEmpty)
            {
                HideAllHints();
                return;
            }

            foreach (var h in _workspace.Hosts)
            {
                if (ReferenceEquals(h, _current.Host))
                {
                    h.ShowDropHint(_current, screen);
                }
                else
                {
                    h.HideHint();
                }
            }
        }

        public void End(PixelPoint screen)
        {
            var panel = _panel;
            var sourceManager = _sourceManager;
            var sourceHost = _sourceHost;
            var current = _current;
            var activated = _active;

            _panel = null;
            _sourceManager = null;
            _sourceHost = null;
            _active = false;
            _current = DropTarget.Empty;

            HideAllHints();

            if (panel is null || sourceManager is null || sourceHost is null)
            {
                return;
            }

            sourceHost.HideGhost();

            // A press that never crossed the threshold is a plain click (selection already happened).
            if (!activated)
            {
                return;
            }

            sourceManager.Detach(panel);

            if (current.Host is DockHost targetHost && !current.IsEmpty)
            {
                var group = current.Group;

                // Docking back into the source host: the captured group may have been pruned by Detach.
                if (ReferenceEquals(targetHost.Manager, sourceManager)
                    && group is not null
                    && targetHost.Manager.Root?.Groups().Any(g => ReferenceEquals(g, group)) != true)
                {
                    group = null;
                }

                targetHost.Manager!.Dock(panel, group, current.Side);
            }
            else
            {
                _workspace.FloatPanel(panel, screen);
            }
        }

        private void HideAllHints()
        {
            foreach (var host in _workspace.Hosts)
            {
                host.HideHint();
            }
        }
    }
}
