// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using System.Threading.Tasks;
using XMAT.Operations;
using XMAT.Operations.Inspection;

namespace XMAT.WebServiceCapture
{
    // A view that shows body previews (BodyPreviewPipeline). Every member is called on the view's UI thread.
    internal interface IBodyPreviewTarget
    {
        // What the view previews now, as a value that Equals another exactly when it names the same inputs (the same
        // objects); null when there is nothing to preview, such as when nothing is selected.
        object InputIdentity { get; }

        // The preview request for the inputs that InputIdentity names now.
        BodyPreviewRequest CreateRequest();

        void ShowNothing();

        void ShowPending();

        void ShowPreview(BodyPreview preview);

        void ShowFailure();

        // Runs the action on the view's UI thread later, once the changes in progress are complete, such as every
        // binding of a new selection.
        void Post(Action action);
    }

    // The one body preview pipeline of a view. An input change cancels the preview in progress, shows that a preview is
    // coming and posts one refresh, so the changes that one selection makes (headers, body, framing) make one preview.
    // The refresh snapshots the inputs and runs one preview of them, unless the result shown, or the preview in
    // progress, is already of them. A result is shown only when its run is still the latest (generation), the view
    // still has the inputs that it previewed (identity) and the view is active (lifetime); otherwise it is dropped. The
    // view keeps the result that it shows and nothing else. Nothing here waits: the operation runs on the thread pool,
    // and its result comes back on the UI thread through the synchronization context that the refresh ran in.
    internal sealed class BodyPreviewPipeline
    {
        private readonly IBodyPreviewTarget _target;
        private CancellationTokenSource _running;
        private object _runningIdentity;
        private object _shownIdentity;
        private bool _shown;
        private int _generation;
        private bool _active;
        private bool _refreshPosted;

        internal BodyPreviewPipeline(IBodyPreviewTarget target)
        {
            _target = target ?? throw new ArgumentNullException(nameof(target));
        }

        // Runs one preview. Tests replace it to decide when previews complete; by default the operation runs it.
        internal Func<BodyPreviewRequest, CancellationToken, Task<OperationResult<BodyPreview>>> Runner { get; set; } =
            RunOperationAsync;

        // The view shows previews again (it was loaded): it previews what it has, unless that is already shown.
        internal void Activate()
        {
            _active = true;
            PostRefresh();
        }

        // The view no longer shows previews (it was unloaded or closed, or the application is exiting): the preview in
        // progress is cancelled, and no result is shown until the view is active again.
        internal void Deactivate()
        {
            _active = false;
            Cancel();
        }

        // An input changed: the result shown and the preview in progress are of other inputs.
        internal void InputChanged()
        {
            Cancel();
            _shown = false;
            _shownIdentity = null;
            if (_target.InputIdentity == null)
            {
                _target.ShowNothing();
            }
            else
            {
                _target.ShowPending();
            }

            PostRefresh();
        }

        private void PostRefresh()
        {
            // One refresh is pending at most: it reads the inputs as they are when it runs.
            if (_active && !_refreshPosted)
            {
                _refreshPosted = true;
                _target.Post(Refresh);
            }
        }

        private void Refresh()
        {
            _refreshPosted = false;
            if (!_active)
            {
                return;
            }

            object identity = _target.InputIdentity;
            if (identity == null)
            {
                Cancel();
                _target.ShowNothing();
                return;
            }

            if ((_shown && Equals(_shownIdentity, identity)) ||
                (_running != null && Equals(_runningIdentity, identity)))
            {
                return;
            }

            Cancel();
            _ = RunAsync(identity, _target.CreateRequest(), ++_generation);
        }

        private async Task RunAsync(object identity, BodyPreviewRequest request, int generation)
        {
            using var cancellation = new CancellationTokenSource();
            _running = cancellation;
            _runningIdentity = identity;
            OperationResult<BodyPreview> result;
            try
            {
                result = await Runner(request, cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // Cancelled because the preview is no longer needed: there is nothing to show or log.
                return;
            }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                // The type only: a message could quote the body.
                PublicUtilities.AppLog(LogLevel.ERROR, $"Body preview failed: {exception.GetType().FullName}");
                result = null;
            }
            finally
            {
                if (ReferenceEquals(_running, cancellation))
                {
                    _running = null;
                    _runningIdentity = null;
                }
            }

            // Only the latest run (generation), of the inputs that the view still has (identity), while the view is active
            // (lifetime), is shown.
            if (generation != _generation)
            {
                return;
            }

            if (!Equals(_target.InputIdentity, identity))
            {
                return;
            }

            if (!_active || result?.Outcome == OperationOutcome.Cancelled)
            {
                return;
            }

            _shown = true;
            _shownIdentity = identity;
            if (result is { Succeeded: true, Value: not null })
            {
                _target.ShowPreview(result.Value);
            }
            else
            {
                _target.ShowFailure();
            }
        }

        private void Cancel()
        {
            if (_running != null)
            {
                _running.Cancel();
                _running = null;
                _runningIdentity = null;
            }
        }

        private static Task<OperationResult<BodyPreview>> RunOperationAsync(
            BodyPreviewRequest request, CancellationToken cancellationToken) =>
            new BodyPreviewOperation().ExecuteAsync(request, OperationContext.None, cancellationToken);
    }
}
