# ColdChainQuotes Assignment 7

**Group IT-2511:** Yesyentai Byerdikhajy, Estay, Byegarys.

This is the same refrigerated-shipment quote project used in Assignments 5 and 6.
Assignment 7 adds a real JSON-to-booking workflow with Result, Option, Map and Bind.
It retains both the earlier quote pipeline and the safe Requested -> Quoted -> Confirmed transitions.
Expected business failures are values. File and JSON exceptions are converted at the application boundary.

## Build run and test

Prerequisites: .NET SDK **10.0.401** (the version used for verification), or a compatible .NET 10 SDK.
No additional runtime or database is needed. The first restore needs internet access to NuGet.org for xUnit,
Microsoft.NET.Test.Sdk and coverlet. NuGet.Config contains only the public package source, no credentials.

After extracting the ZIP, open a terminal in the **ColdChainQuotes** folder that contains
ColdChainQuotes.slnx and README.md. Run:

```text
dotnet restore
dotnet build --no-restore
dotnet test --no-restore
```

All commands below start in that same solution folder. The --project option deliberately points to
the nested application folder; sample paths are relative to the solution folder.

```text
# A7 normal workflow, all three domain errors, present and absent lookups
dotnet run --no-restore --project ColdChainQuotes -- result ColdChainQuotes/samples/shipments.json

# A7 single successful request; exit 0
dotnet run --no-restore --project ColdChainQuotes -- result ColdChainQuotes/samples/shipments.json SH1 standard

# A7 missing required booking; exit 1
dotnet run --no-restore --project ColdChainQuotes -- result ColdChainQuotes/samples/shipments.json UNKNOWN standard

# A7 unknown policy; exit 1
dotnet run --no-restore --project ColdChainQuotes -- result ColdChainQuotes/samples/shipments.json SH1 unknown

# A7 missing file; exit 1 (this file is deliberately absent)
dotnet run --no-restore --project ColdChainQuotes -- result ColdChainQuotes/samples/missing.json

# A7 malformed JSON; exit 1
dotnet run --no-restore --project ColdChainQuotes -- result ColdChainQuotes/samples/malformed.json

# A7 valid JSON with invalid domain data in the last row; exit 1
dotnet run --no-restore --project ColdChainQuotes -- result ColdChainQuotes/samples/invalid-shipment.json

# Earlier A5 pure pipeline remains active: totals 25500 and 38050
dotnet run --no-restore --project ColdChainQuotes -- ColdChainQuotes/shipments.txt both

# Earlier A6 safe booking transitions remain active
dotnet run --no-restore --project ColdChainQuotes -- book ColdChainQuotes/shipments.txt ColdChainQuotes/actions.txt
```

For the default demonstration, the expected output is:

```text
Cold chain Result workflow
SH5: present, distance 0 km
UNKNOWN: missing
SUCCESS SH1 confirmed as #1, 4400 (standard)
ERROR [missing_booking] No open booking for 'UNKNOWN' (unknown or outside 300 km).
ERROR [invalid_policy] Unknown policy 'unknown'; use standard or express.
ERROR [state_rejected] SH1 is confirmed; its price can't change
Original state: Requested; successful state: Confirmed
```

The demonstration deliberately runs both successful and rejected requests and exits 0 when its
input loads. A single-request failure or a file-loading failure exits 1. There is no silent fallback.
Malformed JSON output includes `malformed_data`, the input path and the parser's diagnostic;
line numbers or wording may vary with the SDK. A missing file reports `file_missing` and the path.

## Domain outcomes

| Outcome | Trigger | Expected result |
|---|---|---|
| Success | SH1, standard, Requested state | Confirmed number 1, price 4400, next number 2 |
| missing_booking | UNKNOWN or shipment over the 300 km service limit | Failure identifies the id; no policy or transition runs |
| invalid_policy | SH1, unknown policy | Failure identifies the policy; booking remains Requested |
| state_rejected | Price an already Quoted or Confirmed booking | Failure identifies the booking and state rule; old price and number remain |
| invalid_shipment | JSON record with weight 0, invalid class, or another factory violation | Failure identifies file, row, shipment and broken rule; no new ledger is committed |
| duplicate_id | JSON rows repeat an id ignoring case | Failure identifies the repeated id; entire import is rejected |

`Result<T>` is a closed hierarchy with `Success(Value)` and `Failure(Error)`. The cases have
get-only properties and private base construction. Only Success has a value; only Failure has an error.
Null required payloads and null errors are rejected as programming mistakes, rather than constructing
contradictory outcomes. A normal domain rejection never throws or depends on a catch.

The earlier `ShipmentCheck` says whether input can create a valid Shipment. The earlier `Booking`
case says which workflow state a valid shipment currently occupies. These represent different facts:
a valid Requested shipment is not a validation failure just because confirmation needs a price first.
`BookingStep` remains the safe transition model; ResultBookings adapts its rejected steps into
structured errors without bypassing its rules.

## Missing values

`ResultBookings.Find` is a real lookup in the ledger's case-insensitive booking dictionary.
It returns `Option<Booking>.Found` or `Missing`. Missing is not represented by a fabricated
booking or by comparing a value with default(T). The booking workflow requires the item, so
`ToResult` converts Missing into `missing_booking` before any dependent operation runs.

SH5 is present with distance **0 km**. The earlier pipeline prices it at **1080** under Standard.
Zero is legitimate data. `Some(0)`, `Some(false)` and `Some("")` are also present values, tested
explicitly. Only the Missing case means absence. Production code uses no null-forgiving operator
in the new Result workflow.

## Result composition

The real chain in `ResultBookings.Book` is:

```text
Find ledger id
  -> Option.ToResult
  -> Bind FindPolicy
  -> Bind PriceAndConfirm
       -> Ledger.Apply Price
       -> Bind Ledger.Apply Confirm
  -> Map immutable BookingReceipt
```

Map takes a pure `T -> U` transformation and returns `Result<U>`; here it creates the receipt
and display text. Bind takes `T -> Result<U>` and continues a computation that may reject;
it does not nest a Result inside a Result. Both copy the same error on failure without invoking
the supplied function. `FailureSkipsMapAndBind` asserts that a counter remains zero and that
the exact error object is preserved. `MissingBookingSkipsInvalidPolicy` shows that an absent
booking wins even when the requested policy is also invalid.

Success payloads are inspected only in a success pattern or success branch. ResultConsole handles
each completed request once with explicit success/failure cases and a clear exit code. It neither
prints inside pure domain methods nor turns a failure into an empty successful receipt.

## Exception boundaries and consistent state

`JsonBoundary.Load` reads and deserializes JSON at the application boundary. `FileNotFoundException`
and `DirectoryNotFoundException` become `file_missing`; `JsonException` becomes `malformed_data`.
Access and other I/O problems become `file_access` or `file_io`; invalid paths become `file_path`.
Every error includes the operation and path. There is no empty catch, catch-all success or hidden fallback.
Domain functions do not perform file I/O.

The JSON root must be an array of records with all five fields. Missing numeric fields are rejected,
so missing distance cannot silently become zero. Null records, wrong JSON types, and unknown fields
are rejected. Storage classes must be named Cold or Frozen, not numeric enum strings. Empty `[]`
is valid empty data; JSON `null` is not. Each row passes through the existing validated Shipment factory.

Rows are collected locally; Ledger.Open runs only after every row validates and ids are unique.
`JsonBoundary.Reload` returns the original ledger object for every failure. A bad later row cannot
commit an earlier row. No stored file is changed by this application. ResultBookings likewise
computes immutable intermediate ledgers and exposes a new ledger only on final success. Failed
requests preserve every existing booking and the next confirmation number.

## Requirement map

| Requirement | Files and methods |
|---|---|
| Explicit success/failure and safe payload access | Outcomes.cs: Result<T>, Success, Failure |
| At least three expected domain failures | ResultBookings.cs: Book, FindPolicy, Accept |
| Real present and absent lookup | ResultBookings.Find; Option<T>.ToResult |
| Valid zero and false retained | Outcomes.cs Option<T>; OutcomeTests.PrimitiveZeroAndFalseArePresent |
| Map and Bind used in application | ResultBookings.Book and PriceAndConfirm |
| Dependent operations skipped on failure | OutcomeTests.FailureSkipsMapAndBind and MissingBookingSkipsInvalidPolicy |
| JSON file and parsing boundary | JsonBoundary.Load |
| No partial import or failed state change | JsonBoundary.Reload; immutable Ledger.Apply; JsonBoundaryTests |
| Final explicit outcome handling | ResultConsole.Run and Show; Program.Main result mode |
| Earlier pipeline and transitions active | Stages.cs, Workflow.cs, Booking.cs; existing tests and commands above |
| Normal and failure sample data | ColdChainQuotes/samples/*.json; earlier shipments.txt and actions.txt |

## Automated tests

Latest verified outcome: **81 passed, 0 failed, 0 skipped**, SDK 10.0.401.
There are 54 cumulative tests from A5/A6 and 27 additional A7 cases (theory rows count as cases).
Tests use xUnit assertions. File tests create unique temporary folders and delete them with IDisposable;
they do not rely on a user's files. Earlier supplied data is copied into the test output by the test project.

| File | Cases | Assertions and expected results |
|---|---:|---|
| OutcomeTests.cs | 11 | Map 4 -> 8; Bind preserves returned Result; failure counter stays 0; null payload protection; Some(0/false/empty); real present/absent lookup; SH1 price 4400 and confirmation 1; missing booking; invalid policy; rejected reprice; missing booking short-circuits policy |
| JsonBoundaryTests.cs | 11 | Real JSON success; missing file; 5 malformed-content/schema cases; bad later row preserves state; duplicate id rejects file; successful reload; empty array |
| ResultConsoleTests.cs | 5 | Demo success and 3 domain errors; missing and malformed file exit 1; Express price 4200; single domain failure exit 1 |
| Earlier four test files | 54 | Existing factory rules, pure pipeline, policy totals, immutable state transitions and action parsing remain covered |

## Individual submission files

Each member uploads one ZIP containing the complete runnable project. The member-specific
`CONTRIBUTION.md` at the archive root identifies the submitter, assigned source files, tests,
and defense topics. `Contributions/` contains all three detailed module scopes for comparison.
The source and tests are shared across the three archives; the submitter identification differs.

## Member contribution scope for Assignment 7

The division below continues the module responsibilities used in Assignment 6. Each member must
review and understand their section and its assertions before the individual defense.
Detailed scopes: [Yesyentai Byerdikhajy](Contributions/Byerdikhajy_Yesyentai.md),
[Estay](Contributions/Estay.md), and [Byegarys](Contributions/Byegarys.md).

These descriptions identify allocated module responsibility. The Assignment 7 implementation
and packaging were prepared with AI assistance. Each member must verify their own contribution
statement and explain the submitted code under the course policy. Allocating a module or naming
a Git commit does not itself establish that a student independently authored it.

| Member and group | Logic scope | Tests | Documentation and defense scope |
|---|---|---|---|
| Yesyentai Byerdikhajy, IT-2511 | ResultConsole.cs and Program.Main integration; final success/failure formatting and exit codes | ResultConsoleTests.cs (5) | Build/run instructions, expected output, application boundary demonstration |
| Estay, IT-2511 | JsonBoundary.cs; strict JSON records, reuse of Shipment.Create, file exception conversion and atomic reload | JsonBoundaryTests.cs (11) | JSON input rules, operational errors and consistent-state explanation |
| Byegarys, IT-2511 | Outcomes.cs and ResultBookings.cs; Result, Option, Map/Bind, real lookup and reuse of Ledger transitions | OutcomeTests.cs (11) | Domain failures, composition, missing values and skipped-operation proof |

## Individual defense walkthrough

1. Run the result demonstration and the malformed JSON command above.
2. Open ResultBookings.Book and trace lookup -> ToResult -> Bind -> Map. Explain why Map wraps
   a plain value while Bind receives an already fallible computation.
3. Run `dotnet test --no-restore --filter FullyQualifiedName~FailureSkipsMapAndBind` and show
   `Assert.Equal(0, calls)`; a failure must never execute those delegates.
4. Explain why SH5 at 0 km exists and UNKNOWN does not; show the two Option cases.
5. Show JsonBoundary.Load's catches and Reload's failure branch. Show the test that rejects a bad
   final row and asserts the original ledger reference is unchanged.
6. Practice a small change: add a named price policy to FindPolicy and test both its price and an
   unknown policy; or add a JSON schema rejection and an assertion that no state is committed.

## Git contribution organization

The earlier combined Assignment 7 commit is retained in history and reversed before the same
implementation is reapplied in three module commits: outcomes/composition, JSON boundaries,
and console integration/documentation. This avoids duplicate active changes and preserves the
published project history. Commit messages identify module allocation; Git author identities
remain the actual configured committing account.

## Repository and submission

Project repository: https://github.com/berdikhajyesentay-afk/ColdChainQuotes
The Git repository retains the project across assignments. The repository link does not replace
this runnable ZIP for LMS submission. The ZIP includes source, solution and project files, tests,
README and sample data. It excludes .git, bin, obj, IDE caches and credentials.

---

# Earlier assignment documentation

The following A5/A6 sections are retained as historical documentation. Their test counts and
latest-run statements refer to those stages; the current A7 count and commands are above.

# ColdChainQuotes

**Team (Group IT-2511):** Yesyentai Byerdikhajy, Estay, Byegarys

Assignment 6: Handling Temporal Coupling. This builds on the Assignment 5 pipeline in the same
project. Targets .NET 10 (tested with SDK 10.0.401). Sections A6.1–A6.6 describe Assignment 6;
sections 1–9 below them describe the Assignment 5 pipeline, which still runs unchanged.

## A6.1 The hidden dependency (before)

The operation is **booking a quote**: a dispatcher prices a shipment with a policy, then confirms
it. Before this assignment the natural way to write it was a mutable desk object whose methods
must be called in the right order. This code is **not** in the project; it is only here to show the
problem.

```csharp
// UNSAFE (documentation only)
class QuoteDesk
{
    Shipment? current;
    Func<Shipment, decimal>? policy;
    decimal price;
    bool confirmed;

    public void Load(Shipment s) => current = s;
    public void UsePolicy(Func<Shipment, decimal> p) => policy = p;
    public void Price() => price = policy!(current!);
    public void Confirm() => confirmed = true;
}

var desk = new QuoteDesk();
desk.Load(sh1);
desk.Confirm();      // forgot UsePolicy() and Price(): SH1 is confirmed with price 0
```

| Required order | What goes wrong if it changes |
|---|---|
| `Load` → `UsePolicy` → `Price` → `Confirm` | `Confirm()` before `Price()` confirms a price of **0**. |
| | `Price()` without `UsePolicy()` crashes with a `NullReferenceException`. |
| | `Load(other)` after `Price()` keeps the old price for a new shipment. |
| | A second `Confirm()` can't be told apart from the first. |
| | `new Shipment(...)` with weight −5 or 10³⁰ is accepted (in Assignment 5 a huge weight crashed `PricePolicies.Standard`). |

Nothing in the API shows the order; you have to remember it.

## A6.2 The safe design (after)

```csharp
// SAFE (the real code path)
ShipmentCheck check = Shipment.Create(id, customer, cls, weightKg, distanceKm);  // factory
Ledger ledger = Ledger.Open(validShipments, 300m);                               // every booking starts Requested
(ledger, step) = Ledger.Apply(ledger, new BookingAction.Price("SH1", "Standard", PricePolicies.Standard));
(ledger, step) = Ledger.Apply(ledger, new BookingAction.Confirm("SH1"));        // only a Quoted booking can be confirmed
```

1. **Validated factory.** `Shipment.Create` (Models.cs) checks every rule and returns
   `ShipmentCheck.Valid` (carries the `Shipment`) or `ShipmentCheck.Invalid` (carries all reasons).
   Rules: id and customer are not blank; the storage class is a declared value; weight is
   greater than 0 and at most **10 000 kg**; distance is 0 or more and at most **20 000 km**.
2. **No unchecked construction.** `Shipment`'s constructor is `private` and its properties have no
   setters, so `Create` is the only way in. `Parsing` uses `Create` for every line, so the real
   workflow only ever sees validated shipments.
3. **Explicit states.** A booking is exactly one of `Booking.Requested`, `Booking.Quoted`
   (carries `PolicyName` and `Price`) or `Booking.Confirmed` (carries `PolicyName`, `Price` and
   `Number`). The state constructors are `internal`, so outside code can reach a state only
   through `BookingRules`.
4. **Pure transitions.** `BookingRules.Price` and `BookingRules.Confirm` take the current state and
   return a `BookingStep`: `Moved` (the new state), `Repeated` (the existing outcome) or
   `Rejected` (the reason). They never change the booking they get.
5. **Commit at the boundary.** `Ledger.Apply` applies a step and returns a new `Ledger` only for
   `Moved`; for `Repeated` and `Rejected` it returns the **same** ledger object, so a failure
   can't leave a half-updated state.

### Cases and what they mean

| Union | Case | Payload | Meaning |
|---|---|---|---|
| `ShipmentCheck` | `Valid` | `Shipment` | every rule passed |
| | `Invalid` | `Reasons` (all of them) | at least one rule failed; no shipment exists |
| `Booking` | `Requested` | `Shipment` | opened for a served shipment, no price yet |
| | `Quoted` | `Shipment`, `PolicyName`, `Price` | priced once, can be confirmed |
| | `Confirmed` | `Shipment`, `PolicyName`, `Price`, `Number` | final |
| `BookingStep` | `Moved` | `Next` | the transition happened |
| | `Repeated` | `Existing` | already done; nothing changed |
| | `Rejected` | `Reason` | prerequisite missing or not allowed; nothing changed |
| `ActionLine` | `Parsed` | `Action` | an actions-file line read as `price` or `confirm` |
| | `Unreadable` | `Reason` | the line can't be used |
| `ActionResult` | `Applied` | `LineNumber`, `Text`, `Step` | the ledger handled the action |
| | `NotUnderstood` | `LineNumber`, `Text`, `Reason` | the ledger never saw it |

Each union has a `private` base constructor, so these are the only cases. Every `switch` names
each case; its `_` fallback rejects ("unknown state", "UNEXPECTED result") and never reports
success.

### Transition rules (including repeats)

| Current state | `price <id> <policy>` | `confirm <id>` |
|---|---|---|
| no booking (unknown id, or over 300 km) | Rejected: `no open booking for <id>` | Rejected: same |
| `Requested` | **Moved** to `Quoted` with the policy's price | Rejected: `<id> has no price yet: price it before confirming` (missing prerequisite) |
| `Quoted` | Rejected: `<id> is already priced (<policy>, <price>)` (a booking is priced once) | **Moved** to `Confirmed #n` |
| `Confirmed` | Rejected: `<id> is confirmed; its price can't change` | **Repeated**: returns the existing confirmation, number unchanged |

Confirmation numbers come from `Ledger.NextConfirmation`, which only grows when a booking is
actually confirmed. There is no clock or random value, so the same files always give the same result.

### Calls the compiler refuses

These are not in the project because they don't compile. Each was checked by building a copy
with the call added to the test project, which is outside code like any other caller:

| Call from outside code | Compiler error |
|---|---|
| `new Shipment("X", "C", StorageClass.Cold, -5m, 10m)` | CS1729: `'Shipment' does not contain a constructor that takes 5 arguments` |
| `shipment with { WeightKg = -5m }` | CS0200: `Property or indexer 'Shipment.WeightKg' cannot be assigned to -- it is read only` |
| `new Booking.Quoted(shipment, "Free", 0m)` | CS1729 (the constructor is internal) |
| `new Booking.Confirmed(quoted, 99)` | CS1729 (the constructor is internal) |
| `record FakeCheck : ShipmentCheck;` | CS7036 (the base constructor is private) |

## A6.3 How to run the booking workflow

From the app folder (`ColdChainQuotes/ColdChainQuotes`):

```
dotnet run -- book                              # shipments.txt + actions.txt
dotnet run -- book shipments.txt empty.txt      # valid empty actions file: every booking stays Requested
dotnet run -- book shipments.txt missing.txt    # missing file: message + exit code 1
```

`actions.txt` has one action per line: `price <id> <standard|express>` or `confirm <id>`. Lines
starting with `#` are comments. The expected results were written down before running:

```
Cold chain bookings
Shipments: shipments.txt   Actions: actions.txt   Max distance: 300 km
Rejected shipment lines: 4

Open bookings: SH1, SH2, SH3, SH5

Actions
  line 2   price SH1 standard   OK        SH1 quoted 4400 (Standard)
  line 3   confirm SH1          OK        SH1 confirmed as #1, 4400
  line 4   confirm SH1          REPEATED  SH1 was already confirmed as #1; nothing changed
  line 5   confirm SH2          REJECTED  SH2 has no price yet: price it before confirming
  line 6   price SH4 express    REJECTED  no open booking for SH4
  line 7   price SH3 express    OK        SH3 quoted 26080 (Express)
  line 8   price SH3 standard   REJECTED  SH3 is already priced (Express, 26080)
  line 9   cancel SH5           INVALID   unknown action 'cancel'. Use price or confirm.

Final bookings
  SH1  Confirmed #1  4400 (Standard)
  SH2  Requested
  SH3  Quoted        26080 (Express)
  SH5  Requested
```

This shows one valid operation (lines 2–3), a repeated operation (line 4), a missing prerequisite
(line 5), a booking that doesn't exist (line 6), a repeated price (line 8) and an unreadable line
(line 9). The plain `dotnet run` quote report from Assignment 5 is unchanged.

## A6.4 Requirement map

| Requirement | Where |
|---|---|
| Hidden dependency, before example | A6.1 (documentation only, not in the code) |
| Validated factory, ≥ 2 checks | `Shipment.Create`, Models.cs |
| No unchecked construction | private `Shipment` constructor, get-only properties; `internal` booking-state constructors |
| Factory used by the real workflow | `Parsing.TryParseShipment` → `Shipment.Create` |
| Discriminated unions | `ShipmentCheck`, `Booking`, `BookingStep` (Models.cs, Booking.cs); `ActionLine`, `ActionResult` (Actions.cs) |
| Pattern matching in the domain path | `Parsing.TryParseShipment`, `BookingRules.Price/Confirm`, `Ledger.Apply`, `Actions.Parse/Run/Describe` |
| Safe operations, explicit states | `BookingRules`, `Ledger.Apply`, Booking.cs |
| Repeated operation rule | `BookingRules.Confirm` → `Repeated`; second `Price` → `Rejected` |
| State preservation | `Ledger.Apply` returns the same ledger for `Repeated`/`Rejected` |
| I/O separated | `Program.RunBookings`: read files → pure core → print |
| Sample input | `shipments.txt`, `actions.txt`, `empty.txt` |

## A6.5 Assignment 6 tests

32 new tests, **54 in total**. **Latest run: Passed 54, Failed 0.**

| Test | File | Required case | Expected |
|---|---|---|---|
| `ValidInputCreatesShipmentWithExpectedData` | ShipmentFactoryTests | factory success | `Valid` with SH1, FreshFarm, Cold, 60, 20 |
| `EachRuleRejectsInvalidInputWithItsReason` (7 cases) | ShipmentFactoryTests | factory rejection | blank id, blank customer, class 99, weight 0, weight 10000.01, distance −5, distance 20000.5: each gives its reason |
| `EveryBrokenRuleIsReportedAtOnce` | ShipmentFactoryTests | factory rejection | 4 reasons |
| `LimitsThemselvesAreAccepted` | ShipmentFactoryTests | boundary value | 10 000 kg, 20 000 km and 0 km are valid |
| `BothCasesAreReachableAndMatchedToTheirPayload` | ShipmentFactoryTests | distinct cases | `valid A` / `invalid: customer is missing` |
| `HugeWeightIsRejectedInsteadOfCrashingThePricePolicy` | ShipmentFactoryTests | factory rejection | the Assignment 5 crash is now a rejected line |
| `NumericStorageClassTextIsRejected` (3 cases) | ShipmentFactoryTests | factory rejection | `0`, `1`, `+1` rejected |
| `LedgerOpensARequestedBookingForEachServedShipment` | BookingTests | normal operation | SH1, SH2, SH3, SH5 Requested (SH4 is over 300 km) |
| `PriceThenConfirmGivesQuotedThenConfirmed` | BookingTests | normal operation | Quoted 4400, then Confirmed #1 |
| `ConfirmBeforePriceIsRejectedAndChangesNothing` | BookingTests | missing prerequisite + state preservation | Rejected; same ledger; SH2 still Requested |
| `SecondConfirmReturnsTheExistingConfirmation` | BookingTests | repeated operation | Repeated #1; ledger unchanged |
| `SecondPriceIsRejectedAndKeepsTheFirstPrice` | BookingTests | repeated operation | Rejected; still 16320 |
| `ShipmentOverTheLimitHasNoBookingToPrice` | BookingTests | boundary / invalid case | SH4 rejected; same ledger |
| `FailedActionsLeaveEveryBookingUnchanged` | BookingTests | state preservation | three failures, same ledger object and entries |
| `EveryStateAndStepIsReachableThroughTheRules` | BookingTests | distinct cases | Requested, Quoted, Confirmed, Moved, Repeated, Rejected |
| `ValidLinesBecomeTypedActions` | ActionTests | normal operation | `PRICE sh1 Express` and `confirm SH1` parsed |
| `BadLinesAreUnreadableWithAReason` (4 cases) | ActionTests | invalid input | unknown action, unknown policy, missing policy, missing id |
| `SampleActionsGiveTheDocumentedOutcomes` | ActionTests | all cases, end to end | the 8 lines and 4 final states in A6.3 |
| `CommentsAndBlankLinesAreSkippedButCounted` | ActionTests | line numbers | the only action is reported as line 3 |
| `RunDoesNotChangeTheStartingLedger` | ActionTests | state preservation | start still all Requested, next number 1 |
| `EmptyActionsFileLeavesAllBookingsRequested` | ActionTests | empty input | no results, all Requested |

## A6.6 Contribution (Assignment 6)

All three members are in group IT-2511.

| Member | Implemented logic | Tests | Documentation |
|---|---|---|---|
| **Yesyentai Byerdikhajy** | `Actions.cs`: `ActionLine` and `ActionResult` unions, `Parse`, `Run`, `Describe`, `DescribeState`; `PricePolicies.Find`; `Program.RunBookings` (the `book` mode) | ActionTests (9) | A6.3 run instructions and expected output |
| **Estay** | `Shipment.Create` factory and `ShipmentCheck` union (Models.cs); `Parsing` now calls the factory and refuses numeric storage classes | ShipmentFactoryTests (15) | A6.1 before example, factory rules |
| **Byegarys** | Booking.cs: `Booking`, `BookingAction`, `BookingStep` unions, `BookingRules.Price/Confirm`, `Ledger.Open/Apply` | BookingTests (8) | A6.2 cases and transition rules |

---

# Assignment 5 (still part of the project)


## 1. Workflow

**Topic.** Delivery quotes for refrigerated (cold chain) shipments. This continues the cold chain
domain from Assignment 4 and is the first working part of the final project.

**Intended users.** A dispatcher at a cold chain logistics company. They get a list of shipment
requests and need a price for each one under the chosen service level.

**Team.** Three members, each owning one part of the code and its tests (see Section 9):
Byegarys (pipeline stages), Estay (models, input parsing and validation), Byerdikhajy (price
policies and console app).

**Input fields.** One shipment per line in `shipments.txt`:

```
Id|Customer|StorageClass|WeightKg|DistanceKm
```

| Field | Type | Rule |
|---|---|---|
| `Id` | text | required, unique (case-insensitive) |
| `Customer` | text | required |
| `StorageClass` | `Cold` or `Frozen` | required; numbers such as `0`, `1` or `99` and lists such as `Cold,Frozen` are rejected (numbers since Assignment 6) |
| `WeightKg` | decimal, `.` as the decimal point | required, must be greater than 0 |
| `DistanceKm` | decimal, `.` as the decimal point | required, must be 0 or more; **0 is a valid value** (pickup and delivery at the same site) |

**Business rules.**
1. Invalid lines are rejected with a clear message and do not reach the pipeline.
2. Only shipments with `DistanceKm <= 300` are served. **300 km itself is served.**
3. The price comes from the chosen policy:
   - **Standard:** `1000 + 50 × km + 40 × kg`, plus `500` for Frozen
   - **Express:** `2000 + 80 × km + 10 × kg`, plus `500` for Frozen
4. Quotes are sorted by price, most expensive first. Equal prices are sorted by Id.

**Expected result.** For each policy, a list of quotes (`Id`, `Customer`, `Price`) and the total.

## 2. Dataset and expected results

I wrote down these results before running the program. `ColdChainQuotes/shipments.txt`:

| Line | Input | What should happen |
|---|---|---|
| 1 | `SH1\|FreshFarm\|Cold\|60\|20` | accepted; heavy and short |
| 2 | `SH2\|IceDream\|Frozen\|5\|40` | accepted; light, farther, Frozen |
| 3 | `SH3\|GreenLeaf\|Cold\|8\|300` | accepted; **boundary**: exactly 300 km is served |
| 4 | `SH4\|NorthFish\|Frozen\|12\|350` | valid but **not served** (over 300 km) |
| 5 | `SH5\|CityLab\|Cold\|2\|0` | accepted; **0 km is a value, not missing** |
| 6 | `SH6\|BadNumber\|Cold\|1,5\|10` | rejected: comma decimal |
| 7 | `SH7\|Negative\|Frozen\|4\|-5` | rejected: negative distance |
| 8 | `SH8\|\|Cold\|3\|15` | rejected: customer missing |
| 9 | `SH9\|WrongClass\|Warm\|3\|15` | rejected: unknown storage class |

| Shipment | Standard | Express |
|---|---|---|
| SH1 Cold 60 kg 20 km | 1000 + 1000 + 2400 = **4400** | 2000 + 1600 + 600 = **4200** |
| SH2 Frozen 5 kg 40 km | 1000 + 2000 + 200 + 500 = **3700** | 2000 + 3200 + 50 + 500 = **5750** |
| SH3 Cold 8 kg 300 km | 1000 + 15000 + 320 = **16320** | 2000 + 24000 + 80 = **26080** |
| SH5 Cold 2 kg 0 km | 1000 + 0 + 80 = **1080** | 2000 + 0 + 20 = **2020** |
| **Total** | **25500** | **38050** |
| **Order** | SH3, **SH1, SH2**, SH5 | SH3, **SH2, SH1**, SH5 |

**Where the policies differ:** Standard charges more per kg, so the heavy SH1 is more expensive
than SH2. Express charges more per km, so SH2 is more expensive than SH1. The same pipeline gives a
different ranking because only the rule changed.

## 3. How to build, run and test

Requirements: .NET SDK 10 (10.0.401 or newer). Unzip the archive, then:

```
cd ColdChainQuotes                              # the folder that holds ColdChainQuotes.slnx
dotnet build
dotnet test                                     # 54 tests (22 from Assignment 5)

cd ColdChainQuotes                              # the app folder; it holds shipments.txt and empty.txt
dotnet run                                      # shipments.txt, both policies
dotnet run -- shipments.txt standard            # one policy: standard | express | both
dotnet run -- empty.txt                         # valid empty dataset
dotnet run -- missing.txt                       # missing input: error message, exit code 1
```

Normal run (`dotnet run`):

```
Cold chain delivery quotes
Input: shipments.txt   Max distance: 300 km

Rejected lines
  line 6   weight '1,5' is not a valid number
  line 7   distance cannot be negative, found -5
  line 8   customer is missing
  line 9   'Warm' is not a declared StorageClass (Cold or Frozen)

Not served (over 300 km)
  SH4  NorthFish  350 km

Standard policy
  SH3  GreenLeaf     16320
  SH1  FreshFarm      4400
  SH2  IceDream       3700
  SH5  CityLab        1080
  Total              25500   (4 quotes)

Express policy
  SH3  GreenLeaf     26080
  SH2  IceDream       5750
  SH1  FreshFarm      4200
  SH5  CityLab        2020
  Total              38050   (4 quotes)
```

This matches Section 2.

Empty dataset (`dotnet run -- empty.txt`): no rejected lines, `no quotes` and `Total 0 (0 quotes)`
for both policies. Missing file (`dotnet run -- missing.txt`):
`Input file not found: ...\missing.txt` and exit code 1. No fake summary is printed.

## 4. Pipeline stages

`Workflow.Run` connects the stages with a small `Pipe` helper, so they read in the order they run:

```csharp
return shipments
    .Pipe(s => Stages.SelectServiceable(s, maxDistanceKm))
    .Pipe(s => Stages.PriceShipments(s, pricePolicy))
    .Pipe(p => Stages.BuildSummary(p));
```

| Stage | Method | Input → Output | Rule | Example |
|---|---|---|---|---|
| 0 (boundary) | `Parsing.ParseLines` | `IEnumerable<string>?` → `ImportResult` | text to valid `Shipment`s plus `ParseError`s | `SH7\|…\|-5` → error, line 7 |
| 1 | `Stages.SelectServiceable` | `IEnumerable<Shipment>` → `ImmutableArray<Shipment>` | keep `DistanceKm <= maxDistanceKm` | SH3 (300 km) kept, SH4 (350 km) dropped |
| 2 | `Stages.PriceShipments` | `IEnumerable<Shipment>` + `Func<Shipment, decimal>` → `ImmutableArray<PricedShipment>` | calls `pricePolicy(s)` once per shipment | SH1 + Standard → 4400 |
| 3 | `Stages.BuildSummary` | `IEnumerable<PricedShipment>` → `QuoteSummary` | price descending, then Id; sum the total | → SH3, SH1, SH2, SH5, total 25500 |

**One record through the pipeline (SH1, Standard):**
`"SH1|FreshFarm|Cold|60|20"` → `Shipment("SH1", "FreshFarm", Cold, 60, 20)` → stage 1 keeps it
(20 ≤ 300) → stage 2: `PricedShipment(SH1, 4400)` → stage 3: `Quote("SH1", "FreshFarm", 4400)`,
second in the list.

## 5. Configurable rule

- **Higher-order function:** `Stages.PriceShipments` takes `Func<Shipment, decimal> pricePolicy` and
  calls it with `pricePolicy(s)` inside `Select`.
- **Two policies:** `PricePolicies.Standard` and `PricePolicies.Express`.
- **Selected outside the pipeline:** `Program.SelectPolicies` turns the command-line word into
  policies. `Main` then passes each one to `Workflow.Run` as an argument. There is no global
  "current policy" field.
- The limit `MaxDistanceKm = 300` is a `const` in `Program` and is also passed as an argument.

## 6. Why the core is pure

- `Parsing`, `Stages`, `PricePolicies` and `Workflow` never touch the console or files, and never
  use the clock, random values or static mutable fields.
- Stages do not modify their input. They read it with LINQ and return a new `ImmutableArray`.
- Results are copies. `QuoteSummary.Quotes` is an `ImmutableArray`, so if the caller changes their
  list later, a summary that is already computed stays the same (test 19).
  `IReadOnlyList` alone would only hide `Add`, and the caller could still change the list underneath.
- All I/O is in `Program.Main`. It reads the file first, then runs the pure core, then prints.
- The only local mutable state is inside `ParseLines` (a builder and a `HashSet` for duplicate Ids).
  It is created and used inside that one call, so nobody outside can see it.

## 7. Requirement map

| Requirement | Where |
|---|---|
| Typed immutable models | `Models.cs`: `Shipment`, `PricedShipment`, `Quote`, `QuoteSummary`, `ParseError`, `ImportResult` (records + `ImmutableArray`) |
| Validation at the boundary, clear messages | `Parsing.TryParseShipment`, `Parsing.ParseLines` |
| Missing vs empty vs zero | `ParseLines(null)` throws; empty file → empty `ImportResult`; empty field → "… is missing"; `0` km accepted |
| No null-forgiving `!`, no silent defaults | `[NotNullWhen(true)]` on `TryParseShipment`; every bad field returns an error |
| Three pure stages, composed | `Stages.cs`, `Workflow.Run` + `PipeExtensions.Pipe` |
| Higher-order function + two policies | `Stages.PriceShipments`, `PricePolicies.Standard` / `Express` |
| Policy chosen outside the pipeline | `Program.SelectPolicies`, `Program.Main` |
| I/O separated | `Program.Main` only |
| Sample input | `ColdChainQuotes/shipments.txt`, `ColdChainQuotes/empty.txt` |
| Tests | `ColdChainQuotes.Tests/ColdChainQuotesTests.cs` |

## 8. Tests

The 22 Assignment 5 tests (15 methods; one `[Theory]` has 8 cases) still pass. **Latest run of all 54: Passed 54, Failed 0.**

| # | Test | Owner | Required case | Expected |
|---|---|---|---|---|
| 1 | `SuppliedFileGivesFiveShipmentsAndFourRejectedLines` | Estay | invalid input | SH1–SH5 valid; errors on lines 6–9 |
| 2 | `StandardPolicyGivesDocumentedQuotes` | Byegarys | normal workflow | SH3, SH1, SH2, SH5; 16320, 4400, 3700, 1080; total 25500 |
| 3 | `ExpressPolicyGivesDocumentedQuotes` | Byegarys | normal workflow | SH3, SH2, SH1, SH5; 26080, 5750, 4200, 2020; total 38050 |
| 4 | `TwoPoliciesOnSameInputChangePricesAndRanking` | Byerdikhajy | two policies | Standard HEAVY > FAR; Express FAR > HEAVY |
| 5 | `EmptyDatasetGivesEmptySummary` | Estay | empty dataset | no shipments, no errors, no quotes, total 0 |
| 6 | `MissingInputIsRejected` | Estay | missing input | `ParseLines(null)` throws `ArgumentNullException` |
| 7 | `DistanceExactlyAtLimitIsServedAndPriced` | Byegarys | boundary | 300 km kept and priced 16040; 300.01 km dropped |
| 8 | `ZeroDistanceIsAValueButAnEmptyDistanceIsMissing` | Estay | missing vs zero | `0` accepted; empty → "distance is missing" |
| 9–16 | `InvalidFieldsAreRejectedWithClearMessage` (8 cases) | Estay | malformed fields | `1,5`, `-5`, empty customer, `Warm`, `99`, `Cold,Frozen`, weight `0`, 4 fields; exact message each time, no `Shipment` |
| 17 | `DuplicateIdIsRejected` | Estay | invalid input | `A` and `a`: second line rejected |
| 18 | `SelectStageDoesNotChangeCallerList` | Byegarys | purity + single stage | caller's list and records unchanged |
| 19 | `ChangingCallerListLaterDoesNotChangeComputedSummary` | Byegarys | purity | summary still A, total 1540 |
| 20 | `PricePolicyIsCalledOncePerSelectedShipment` | Byerdikhajy | higher-order function | called for SH1, SH2, SH3, SH5 only (not SH4) |
| 21 | `SameInputAndPolicyGiveSameOutput` | Byerdikhajy | purity | two runs give equal quotes and total |
| 22 | `EqualPricesAreOrderedById` | Byegarys | stage 3 | C (900), then A and B (500) |

## 9. Contribution

All three members are in group IT-2511. Each member owns the files below, wrote the listed tests,
and documented their part in this README.

| Member | Implemented logic | Tests | Documentation |
|---|---|---|---|
| **Byegarys** | `Stages.cs`: `SelectServiceable`, `PriceShipments`, `BuildSummary`; `Workflow.cs`: `Run` and the `Pipe` helper | 2, 3, 7, 18, 19, 22 | Sections 2 and 4 (dataset, expected results, stages) |
| **Estay** | `Models.cs` (all records, `ImmutableArray`); `Parsing.cs`: `ParseLines` (missing vs empty input, line numbers, duplicate Ids) and field validation (`TryParseShipment`, `TryParseStorageClass`, `TryParseNumber`, `TryReadNumber`, all error messages); `shipments.txt` and `empty.txt` | 1, 5, 6, 8, 9–16, 17 | Sections 1, 6 and 7 (input fields, purity, requirement map) |
| **Yesyentai Byerdikhajy** | `PricePolicies.cs`: `Standard`, `Express`; `Program.cs`: policy selection, file reading, missing-file check, output | 4, 20, 21 | Sections 3 and 5 (run commands, demos, configurable rule); README editing |
