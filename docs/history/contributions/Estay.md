# Assignment 7 contribution scope

**Member:** Estay  
**Group:** IT-2511  
**Project:** ColdChainQuotes

## Assigned module

JSON loading, explicit operational failures, and atomic data import. This section identifies
the module allocated to Estay for review and individual defense in the shared team project.

## Logic and files

- `ColdChainQuotes/JsonBoundary.cs`: read shipment JSON, deserialize strictly, reject missing
  fields and invalid records, and reuse the validated `Shipment.Create` factory from Assignment 6.
- `Load` converts file-not-found and missing-directory exceptions into `file_missing`, JSON parsing
  exceptions into `malformed_data`, and access or I/O exceptions into useful operational errors.
- Domain-invalid records and case-insensitive duplicate ids become explicit failures with file,
  row and shipment context. Every row validates before a new ledger is constructed.
- `Reload` returns the existing ledger object on failure, preventing a partial update.
- `ColdChainQuotes/samples/`: normal JSON, malformed JSON, invalid-domain JSON, and an empty array.

## Automated tests

`ColdChainQuotes.Tests/JsonBoundaryTests.cs` contains **11 test cases**: real JSON booking success,
a missing file, five malformed/schema cases, invalid later row with no partial commit, duplicate
ids, successful atomic reload, and an empty valid array. Each file test creates and cleans up its
own temporary folder.

From the solution folder:

```text
dotnet test --no-restore --filter FullyQualifiedName~JsonBoundaryTests
```

Expected: **11 passed, 0 failed**. The full shared submission contains 81 cumulative test cases.

## Documentation and defense

Explain the README section Exception boundaries and consistent state, identify the relevant
catch clauses, and distinguish malformed input from a valid JSON record that breaks a domain rule.
Show `InvalidLaterRowDoesNotCommitEarlierValidRow` and the assertion that the ledger reference
is unchanged. Demonstrate both missing-file and malformed-file commands from the README.

The final shared application uses this module with the outcome/composition module allocated
to Byegarys and the console module allocated to Yesyentai Byerdikhajy. All required project
files are included in every member's ZIP.
