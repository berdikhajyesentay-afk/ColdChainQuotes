# ColdChainQuotes

**Team (Group IT-2511):** Yesyentai Byerdikhajy, Estay, Byegarys

Assignment 5: Composing Functions Using Pipelining. Targets .NET 10 (tested with SDK 10.0.401).

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
| `StorageClass` | `Cold` or `Frozen` | required; numbers such as `99` and lists such as `Cold,Frozen` are rejected |
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
dotnet test                                     # 22 tests

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
  list later, a summary that is already computed stays the same (test 12).
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

22 xUnit tests (15 methods; one `[Theory]` has 8 cases). **Latest run: Passed 22, Failed 0.**

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
