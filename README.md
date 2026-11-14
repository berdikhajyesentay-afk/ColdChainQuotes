# ColdChainQuotes

A C# console application for a small **refrigerated (cold-chain) delivery company**. It quotes deliveries,
books and confirms them, dispatches them on refrigerated vehicles, checks the temperature log on delivery,
and invoices customers. It is built in a functional style: a pure core of immutable values and rules, with
files and the console kept at the edges.

| | |
|---|---|
| Course | Functional Programming in C#, Final Exam |
| Group | IT-2511 |
| Team | Yesyentai Byerdikhajy, Bekarys Kuvandyk, Estai |
| Repository | https://github.com/berdikhajyesentay-afk/ColdChainQuotes |
| Submitted version | branch `main`, tag `final-submission` (see [Submitted version](#submitted-version)) |

> **Status while the work is in progress.** W3 Dispatch (Bekarys) and W4 Delivery (Estai) are still stubs in
> `Dispatch.cs` and `Delivery.cs`. The commands exist and print `not implemented yet`. This README is updated
> when their pull requests are merged.

## Problem, users and goals

A dispatcher at a cold-chain carrier must answer, for every customer request: *what does it cost, can we
take it, which truck carries it, did it arrive cold enough, and what do we bill?* Mistakes are expensive.
A frozen load in a truck without a freezer, a second confirmation of the same booking, or a refund applied
twice all cost the company money.

- **Users:** the dispatcher (quotes, bookings, dispatch), the warehouse (delivery check), and accounting (invoices, reports).
- **Goals:** each step is checked before it changes anything. A failed step leaves the saved data exactly as it
  was. The same input always gives the same answer, so every rule is tested automatically.

## The five workflows: one booking lifecycle

```
Requested --price--> Quoted --confirm--> Confirmed --dispatch--> Dispatched --deliver--> Delivered --invoice--> Invoiced
                                                                                \---------> Spoiled ---invoice--> Credited
```

| # | Workflow | Input, rules, result | Command | Owner |
|---|---|---|---|---|
| W1 | **Quote pipeline** | shipments file, then rejected lines, distance limit 300 km, price policy, then a sorted quote summary | `quote [file] [standard\|express\|both]` | Bekarys (stages), Estai (parsing), Yesyentai (policies, CLI) |
| W2 | **Price and confirm a booking** | a booking id and a policy, then the booking must be open and not yet priced, then Confirmed #n (Result with Map/Bind) | `confirm <id> [policy]`, `book [shipments] [actions]`, `result` | Bekarys (states, Result), Yesyentai (actions, console), Estai (validated factory, JSON) |
| W3 | **Dispatch** | a confirmed booking and a vehicle, then freezer, capacity and native distance-tier range checks, then Dispatched | `dispatch <id> <vehicle>`, `dispatch auto <id> [firstfit\|bestfit]`, `dispatch fleet` | Bekarys (pending) |
| W4 | **Delivery check** | a dispatched booking and a temperature log (lazy CSV), then the safe range under the strict or tolerant policy, then Delivered or Spoiled | `deliver <id> [strict\|tolerant] [readings]`, `deliver report` | Estai (pending) |
| W5 | **Invoice** | a customer and a discount, then Delivered bookings are billed and Spoiled ones credited once, then an invoice is saved and exported | `invoice <customer> [loyalty\|bulk\|none] [--export file]` | Yesyentai |

Two reports (`report`): **revenue by customer** and **cold-store priority** (in two configured scorings). W3 and W4
add `dispatch fleet` (load per vehicle) and `deliver report` (breaches by storage class).

## Build, run and test

| Prerequisite | Version used |
|---|---|
| .NET SDK | 10.0.401 (`dotnet --list-sdks`), target `net10.0`, RID win-x64 |
| C compiler (only to rebuild the native DLL) | gcc 15.2.0, MSYS2 MinGW-w64, target `x86_64-w64-mingw32` |
| OS | Windows 11 x64 |

From the folder that contains `ColdChainQuotes.slnx`:

```text
dotnet restore
dotnet build
dotnet test                      # all tests, xUnit
cd ColdChainQuotes
dotnet run -- help               # every command
dotnet run -- menu               # numbered menu that runs the same commands
```

Paths are relative to the `ColdChainQuotes` project folder: sample data is in `samples/`, and the saved state is
`data/state.json` (created on first use from `samples/shipments.json`, and git-ignored). Any state command accepts
`--state <path>` and `--seed <path>`, or the `CCQ_STATE` environment variable, so runs are reproducible and tests
never touch the real file. `dotnet run -- reset` starts again from the seed.

### Native library (C, P/Invoke)

`native/ccq_native.c` and `native/ccq_native.h` export `int32_t ccq_distance_tier(int32_t distance_km)` (cdecl),
which returns 0 Local (0–50 km), 1 Regional (51–150 km), 2 LongHaul (151–300 km), or −1 when the distance is out of range.
The compiled `native/ccq_native.dll` (win-x64) is committed and is copied next to the app and the tests by the
csproj files, so building does not need gcc. To rebuild it:

```text
native\build.cmd
rem = gcc -std=c11 -Wall -Wextra -O2 -shared -static-libgcc -o native\ccq_native.dll native\ccq_native.c
```

The binding is `NativeTier` (`[DllImport("ccq_native", CallingConvention = Cdecl)]`). The adapter validates the
distance first and turns `DllNotFoundException`, `BadImageFormatException` and `EntryPointNotFoundException`
into `Result` failures (`native_load_failed`, `native_binding_failed`). The tier is shown in the priority
report and decides `out_of_range` in W3 Dispatch.

## Sample runs (real output)

```text
$ dotnet run -- quote shipments.txt both
Rejected lines
  line 6   weight '1,5' is not a valid number
  line 7   distance cannot be negative, found -5
  line 8   customer is missing
  line 9   'Warm' is not a declared StorageClass (Cold or Frozen)
Not served (over 300 km)
  SH4  NorthFish  350 km
Standard policy                         Express policy
  SH3  GreenLeaf     16320                SH3  GreenLeaf     26080
  SH1  FreshFarm      4400                SH2  IceDream       5750
  SH2  IceDream       3700                SH1  FreshFarm      4200
  SH5  CityLab        1080                SH5  CityLab        2020
  Total              25500                Total              38050
```
(The two policy columns are shown side by side here; the program prints them one after the other.)

```text
$ dotnet run -- reset
SUCCESS state data/state.json reset from samples/shipments.json (4 open bookings)
$ dotnet run -- confirm SH1 standard
SUCCESS SH1 confirmed as #1, 4400 (Standard)
$ dotnet run -- confirm SH1 express                 # expected failure: invalid order
ERROR [state_rejected] SH1 is confirmed; its price can't change
$ dotnet run -- confirm SH9                         # expected failure: missing lookup
ERROR [missing_booking] No open booking for 'SH9' (unknown or outside 300 km).
$ dotnet run -- invoice FreshFarm loyalty           # expected failure: nothing delivered yet
ERROR [nothing_to_bill] 'FreshFarm' has no delivered or spoiled booking yet (SH1 confirmed).
$ dotnet run -- status
  SH1   FreshFarm  Confirmed #1  4400 (Standard)
  SH2   IceDream   Requested
  SH3   GreenLeaf  Requested
  SH5   CityLab    Requested
```

A successful invoice. This is the test `InvoiceCommandSavesTheStateAndExportsTheSameLines`, whose saved state has two
Delivered bookings and one Spoiled booking for FreshFarm:

```text
$ dotnet run -- invoice FreshFarm loyalty --export invoice.txt
Invoice INV-0002 for FreshFarm (Loyalty discount)
  Shipment  Conf Kind       Amount  Note
  A1          #1 charge       4400  delivered by V1
  A2          #2 credit      -2400  reading 2 at 9.5 C is above 8 C
  A4          #4 charge       2400  delivered by V1
  Subtotal                    6800
  Credit                     -2400
  Discount                    -340
  Total                       4060
SUCCESS INV-0002 saved, exported to invoice.txt
```

```text
$ dotnet run -- report
Report 1: revenue by customer (Standard policy, served shipments up to 300 km)
Customer     Shipments  WeightKg   Revenue
GreenLeaf            1         8     16320
FreshFarm            2        70     11300
IceDream             1         5      3700
CityLab              1         2      1080
Total                5        85     32400

Report 2a: cold-store priority, weight first (10 per kg + 1 per km)
Rank Id    Customer     Tier          Score
   1 SH1   FreshFarm    Local           620
   2 SH3   GreenLeaf    LongHaul        380
   3 SH5   CityLab      Local            20

Report 2b: cold-store priority, distance first (1 per kg + 10 per km)
   1 SH3   GreenLeaf    LongHaul       3008
   2 SH1   FreshFarm    Local           260
   3 SH5   CityLab      Local             2
```

With an empty dataset (`report samples/header-only.csv`), every report prints `no records` and the command exits 0.

## Architecture

```
            files / console (boundaries: I/O, exceptions -> Result)
  Program.Run ─┬─ quote/book ─ Parsing ───────────┐
               ├─ result/confirm ─ JsonBoundary ──┤        pure core (no I/O, immutable)
               ├─ report ─ CsvBoundary (lazy) ────┼──>  Shipment.Create, Stages/Workflow, PricePolicies,
               ├─ dispatch ─ DispatchConsole ─────┤     BookingRules/Ledger, ResultBookings, Reports,
               ├─ deliver ─ DeliveryConsole ──────┤     Scoring, Invoicing/Discounts, DispatchRules,
               ├─ invoice ─ InvoiceConsole ───────┤     DeliveryRules
               └─ status/reset ─ StateConsole ────┘
                       │  StateStore (data/state.json)  ReportExport (temp file, then replace)
                       └─ NativeTier ── P/Invoke ── native/ccq_native.dll
```

- **Domain models:** `Shipment` (validated request), `Booking` (closed state hierarchy, one case per lifecycle
  state), `Ledger` (all bookings plus the confirmation and invoice counters), `Invoice`/`InvoiceLine`. W3 adds `Vehicle`,
  and W4 adds `TemperatureReading`.
- **Data flow of a state command:** `StateStore.LoadOrSeed` gives a `Result<Ledger>`. That is bound to the pure rule, which gives a
  `Result<(Ledger, message)>`, then bound to `StateStore.Save`. Only a fully successful chain writes the file.
- **Pure vs side effects:** rules never touch `Console`, files or the clock. All I/O and every `try/catch` is in the
  `*Console`, `*Boundary`, `StateStore`, `ReportExport` and `NativeTier` adapters.

## Functional techniques: where to find them

| Requirement | Implementation |
|---|---|
| Pure calculations, validation, transformations, reports | `Stages`, `PricePolicies`, `BookingRules`, `Reports`, `Invoicing.Compose`, `Discounts` |
| Immutable values; caller data preserved | `sealed record`s, `ImmutableArray`, `ImmutableSortedDictionary`; `Ledger.Apply`/`Invoicing.Build` return a new Ledger (tests check the old one is unchanged) |
| Meaningful pattern matching | `BookingRules.Price/Confirm`, `Actions.Describe` (property patterns), `Invoicing.Billable` (tuple pattern), `StateStore.ToRow` |
| Pipeline with 3 named stages | `Workflow.Run`: `Stages.SelectServiceable` → `PriceShipments` → `BuildSummary` via the `Pipe` extension |
| Higher-order function varying a rule | `Stages.PriceShipments(…, Func<Shipment, decimal> pricePolicy)`; `Reports.ColdPriority(…, score)`; `Invoicing.Build(…, DiscountRule)` |
| Two policies, documented difference | `PricePolicies.Standard` vs `Express`: totals 25500 vs 38050 on `shipments.txt`; `Discounts.Loyalty` vs `Bulk`: 300 vs 0 on 6000, 600 vs 1200 on 12000 |
| Validated factory, no unchecked construction | `Shipment.Create` → `ShipmentCheck.Valid/Invalid` (private constructor); Booking states have internal constructors |
| Distinct cases with payloads | `ShipmentCheck`, `BookingStep.Moved/Repeated/Rejected`, `ActionResult`, `Booking.*`, `Result.Success/Failure`, `Option.Found/Missing` |
| Invalid sequence rejected, no partial state | confirm before price → `Rejected`; price after confirm → `state_rejected`; invoice twice → `already_invoiced`; failed save/export leaves `state.json` byte-identical (tests) |
| Result with ≥3 domain failures | `missing_booking`, `invalid_policy`, `state_rejected`, `missing_customer`, `nothing_to_bill`, `already_invoiced`, `invalid_discount` |
| Map and Bind in a workflow; failure stops later steps | `ResultBookings.Book`, `Invoicing.Build`, `InvoiceConsole.Run`, `StateConsole.Execute` |
| Option for missing lookups | `ResultBookings.Find`, `Invoicing.FindCustomer`, `Discounts.Find` |
| File and parse exceptions at boundaries | `JsonBoundary.Load`, `CsvBoundary.Materialise`, `StateStore.Load/Save`, `ReportExport.Write` (`file_missing`, `malformed_data`, `file_access`, `save_failed`, `export_failed`) |
| ≥2 custom extension methods, used | `PipeExtensions.Pipe` (W1), `ShipmentQueries.ByStorageClass` (reports), `ResultRules.Require` (W5) |
| LINQ: filter, projection, order + tie-breaker, GroupBy, aggregate | `Reports.RevenueByCustomer` (Where, GroupBy, Sum, OrderByDescending.ThenBy customer), `Reports.ColdPriority` (Select with index, ThenBy id), `Invoicing.Compose` (Sum) |
| Empty data, no matches, ties | `no records` lines; tests `AHeaderOnlyInputPrintsNoRecordsInEveryReport`, `ADatasetWithNoColdShipmentsPrintsNoRecordsOnlyInThePriorityReports`, `EqualRevenueIsOrderedByCustomerName…` |
| Curried function + 2 partial applications | `Scoring.CreateScore(10)(1)` / `(1)(10)` in the reports; `Discounts.Create(5000)(5)` = Loyalty, `(10000)(10)` = Bulk |
| `yield return`, deferred execution, deliberate materialisation | `CsvBoundary.StreamRows` (opens nothing until enumerated) and `Materialise` (`ToArray` once); tests count opens and disposals |
| Load JSON/CSV, save and export | `samples/*.json`/`*.csv` in; `StateStore.Save` (state) and `ReportExport.Write` (reports, invoices) out |
| Native C + P/Invoke used in a calculation | `NativeTier.Classify` in the priority report and the dispatch range rule; `NativeTierTests` (real call and injected failure) |

## Persistence, errors and resources

- **Formats:** `samples/shipments.json` (seed), `samples/shipments.csv` (reports), `shipments.txt` and `actions.txt`
  (W1/W2 files), and `data/state.json` (saved lifecycle). W3 and W4 add `samples/vehicles.json` and `samples/readings.csv`.
- **Validation on load:** every saved booking is rebuilt through `Shipment.Create` and the state chain
  (`Quoted → Confirmed → Dispatched → …`), so a hand-edited state file cannot contain an impossible state. Unknown states,
  missing fields, duplicate ids or confirmation numbers, and counters lower than the data are rejected as `malformed_data` / `invalid_shipment` / `duplicate_id`.
- **Resource ownership:** `CsvBoundary.StreamRows` owns its reader (`using var`), so the file is closed when iteration ends,
  fails or stops early. `StateStore.Save` and `ReportExport.Write` own a temporary file. They write it completely, replace the target,
  and delete the temporary file on failure, so the old file is never half-written.
- **Materialisation:** CSV rows are materialised once (`ToArray`) before validation and reports, so repeated enumeration never
  re-reads the file. Report output is materialised into a list so the console and the export show exactly the same lines.
- **Data retention:** the state file holds only shipment and booking data (no personal data beyond a customer name). `reset` discards it.

## Testing

`dotnet test` runs the xUnit suite (163 tests at the time of writing). The suites are:

| File | Covers |
|---|---|
| `ColdChainQuotesTests`, `ShipmentFactoryTests` | pipeline stages, policies, parsing, factory boundaries |
| `BookingTests`, `ActionTests` | safe transitions, repeated and rejected actions, unchanged ledger |
| `OutcomeTests`, `ResultConsoleTests`, `JsonBoundaryTests` | Result/Option, Map/Bind short-circuit, JSON file errors |
| `CsvBoundaryTests`, `QueryTests`, `ReportTests` | lazy CSV, open/dispose counts, extensions, reports, empty and tie cases, export |
| `NativeTierTests` | a real native call, and injected load and binding failures |
| `StateStoreTests` | new lifecycle states, save/load round trip, seeding, malformed state, failed save, `confirm`/`status`/`reset`/`menu` commands |
| `InvoicingTests` | curried discounts, billing and crediting once, the three failure codes, full `invoice` command with export and failures |
| `DispatchTests`, `FleetTests` (W3, pending) / `DeliveryTests`, `ReadingsTests` (W4, pending) | added by Bekarys and Estai |

## Responsibilities

| Area | Files | Owner |
|---|---|---|
| Data model and parsing (A5), validated factory (A6) | `Models.cs`, `Parsing.cs` | Estai |
| JSON boundary (A7) | `JsonBoundary.cs`, `samples/*.json` | Estai |
| Extension method, lazy CSV, resource ownership (A8) | `Queries.cs` (`ByStorageClass`, `Require`), `CsvBoundary.cs` | Estai |
| **W4 Delivery check (final)** | `Delivery.cs`, `Readings.cs`, `samples/readings.csv` | Estai (pending) |
| Pipeline stages and workflow (A5) | `Stages.cs`, `Workflow.cs` | Bekarys |
| Booking states and safe transitions (A6) | `Booking.cs` (Requested/Quoted/Confirmed, `BookingRules`, `Ledger`) | Bekarys |
| Result/Option and composition (A7) | `Outcomes.cs`, `ResultBookings.cs` | Bekarys |
| Native C library and P/Invoke (A8) | `native/ccq_native.c`, `NativeTier.cs` | Bekarys |
| **W3 Dispatch (final)** | `Dispatch.cs`, `Fleet.cs`, `samples/vehicles.json` | Bekarys (pending) |
| Price policies and CLI (A5), actions file (A6), result console (A7) | `PricePolicies.cs`, `Program.cs`, `Actions.cs`, `ResultConsole.cs` | Yesyentai |
| Curried scoring, reports, export (A8) | `Scoring.cs`, `Reports.cs`, `ReportConsole.cs`, `ReportExport.cs` | Yesyentai |
| **Shared foundation (final)** | `Booking.cs` (Dispatched…Credited, `NextInvoice`), `StateStore.cs`, `StateConsole.cs`, `Menu.cs`, `Program.Run`, `native/ccq_native.h` | Yesyentai |
| **W5 Invoice (final)** | `Invoicing.cs`, `InvoiceConsole.cs` | Yesyentai |
| Final README, group report | `README.md`, `docs/` | Yesyentai (with team review) |

Each member's tests are in the test files listed next to their workflow. Earlier per-assignment documentation and
contribution notes are kept in [`docs/history/`](docs/history/).

## Limitations

- **W3 and W4 are pending** until the teammates' pull requests are merged; until then the commands print `not implemented yet`.
- **Windows x64 only** for the committed DLL. Another platform needs a rebuild with the same gcc command (`.so`/`.dylib`).
- **Whole kilometres:** the native tier accepts whole kilometres from 0 to 300.
- **Single user:** the state file assumes one user at a time (there is no locking between concurrent runs).
- **`result` is a demonstration:** without arguments it runs a fixed demonstration of every A7 outcome, and `confirm` is the everyday command.

## References

- .NET 10 base library only (`System.Text.Json`, `System.Collections.Immutable`); xUnit 2.9 for tests; MinGW-w64 gcc for the native DLL.
- Course materials for Assignments 5–8 and the final exam brief.

## Submitted version

| | |
|---|---|
| Repository | https://github.com/berdikhajyesentay-afk/ColdChainQuotes |
| Branch | `main` |
| Tag | `final-submission` |
| Commit | `<FINAL_COMMIT>`. A commit cannot contain its own hash, so the full hash of the tagged commit is recorded in the group report and in `GIT_LINK.txt` in each submission zip. Check it with `git rev-parse final-submission`. |
