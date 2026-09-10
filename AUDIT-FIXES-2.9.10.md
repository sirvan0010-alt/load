# MailLoadTester 2.9.10 — audit fixes

## Fixed: SmartPaceController cancellation race

`SmartPaceController` previously used `RemoveLast()` when a worker cancelled its wait.
That was unsafe because the worker did not retain the identity of its reservation. Another
worker could reserve a later slot before cancellation, causing the wrong worker's slot to be
removed.

### Fix

Each global reservation now retains its exact `LinkedListNode<long>`. Cancellation removes
that node only if it is still attached to the controller's schedule. Successful completion
also removes the same node.

### Regression tests

`SmartPaceControllerTests` now verifies:

1. two workers can reserve two slots;
2. cancelling worker #1 removes exactly one reservation;
3. worker #2's reservation remains scheduled;
4. cancelling worker #2 then leaves an empty schedule;
5. a successful wait leaves no stale reservation.

## Documentation consistency

The package version is now consistently **2.9.10** in `VERSION`, the build documentation,
tempo specification and Inno Setup template. The previously versioned README was renamed
from `README-2.9.4.md` to `README-2.9.10.md`.

## Test execution limitation

The source was statically reviewed and the regression tests were added, but this environment
does not contain the .NET 8 SDK (`dotnet` is unavailable). Therefore no claim is made that
`dotnet test` passed here. Run the project's normal test command on a Windows/.NET 8 machine
before treating this build as a release.
