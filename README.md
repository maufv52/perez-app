# Perez App - Ventas Movil

Spanish inventory, sales, and customer-request UI with an ASP.NET Core 10 API
and shared Azure SQL persistence.

## Source of truth

Use this repository for application changes. Work on a branch, open a pull
request against `main`, and merge only after build/tests pass. Do not upload
only the three frontend files to Azure: publish the complete backend and assets.

The integration retains the newer logo, receipt/PDF controls, and product sale
prices while preserving server-backed login, permissions, CSRF, and shared data.
Only Masteruser manages inventory. Heilyn and Marcelina remain Sellers.
Passwords are salted hashes in SQL, never frontend constants. Existing public
demo passwords must not be used for real customer data.

The old browser-only localStorage implementation is not used or automatically
migrated. Existing database records and account passwords are not reset during
deployment.

## Local build and tests

Install .NET SDK 10.0.402 (or a newer patch in the same feature band). NuGet uses
the existing configured feeds and the repository's CFS feed; public nuget.org is
disabled. Do not bypass the feed or its package quarantine.

```powershell
dotnet restore .\tests\KevinSite.Tests.csproj
dotnet test .\tests\KevinSite.Tests.csproj --no-restore
node --check .\script.js
dotnet publish .\server\KevinSite.csproj -c Release -r win-x86 --self-contained false -o "$env:TEMP\perez-app-publish"
```

The normal tests use an in-memory store and test-only accounts. SQL integration
tests are skipped unless explicitly configured with `KEVIN_TEST_DB`. The optional
live HTTP journey additionally requires `KEVIN_TEST_BASEURL` and
`KEVIN_TEST_DEMO_ZIP`; use only an authorized test environment. These tests create
and clean their own uniquely identified records. Never store connection strings
or seed ZIPs in Git.

To run against an already-configured, explicitly authorized database, provide
`ConnectionStrings__AppDb` through the process environment and run:

```powershell
dotnet run --project .\server\KevinSite.csproj --no-launch-profile
```

The existing Azure App Service already supplies its managed-identity SQL
connection. That backend identity is separate from app-user login and from the
GitHub deployment identity. Infrastructure and privileged database administration
are managed separately; CI does not provision resources or initialize SQL.

Product `salePrice` is optional for legacy inventory, validated server-side,
and stored in the existing product JSON. No schema migration is needed. New
inventory requires a sale price in the UI and selecting it prefills the sale
form; the seller can still edit the transaction's actual price.

The receipt's Print and PDF buttons open the browser/system print dialog in
58 mm format. Select a printer or Save as PDF there; browsers do not let a website
silently choose a printer or a file destination.

## GitHub Actions setup - deployment starts disabled

Pull requests and pushes to `main` build/test and package the complete Windows
x86 application. Deployment is **skipped** until the repository variable
`AZURE_DEPLOY_ENABLED` is exactly `true`.

The engineer must configure Azure authentication; this change does not create
an identity, grant Azure roles, or enable deployment.

1. Create the GitHub environment **production**. Under deployment branches,
   allow only `main`; add a required reviewer if desired. Protect `main` with
   pull-request reviews and the **Build and test** status check.
2. In the explicitly approved Azure tenant, configure a dedicated secretless
   deployment identity. Add an OIDC federated credential:
   - Issuer: `https://token.actions.githubusercontent.com`
   - Subject: `repo:maufv52@335769643/perez-app@1422757039:environment:production`
   - Audience: `api://AzureADTokenExchange`

   This repository uses GitHub's immutable OIDC subject, including the owner
   and repository IDs. Confirm the prefix with
   `gh api repos/maufv52/perez-app/actions/oidc/customization/sub` and append
   `:environment:production`. Do not substitute the older name-only format:
   Azure requires an exact subject match. Never print the complete OIDC token.
3. Grant that identity **Website Contributor** at the existing Web App's
   resource scope only, not at subscription or resource-group scope. It needs no
   SQL permissions. Retain disabled basic publishing authentication.
4. Add the following **production environment variables** under GitHub Actions:

   | Variable | Value |
   |----------|-------|
   | `AZURE_CLIENT_ID` | Deployment identity client ID |
   | `AZURE_TENANT_ID` | Explicitly approved tenant ID |
   | `AZURE_SUBSCRIPTION_ID` | Existing website subscription ID |
   | `AZURE_RESOURCE_GROUP` | `rg-kevin-site` |
   | `AZURE_WEBAPP_NAME` | `app-kevin-site-dev-b931` |

   No client secret, publishing password, SQL password, or demo login password
   is required. Do not paste credentials into issues, commits, or workflow logs.
5. After reviewing a successful build and the environment restrictions, set the
   **repository-level** variable `AZURE_DEPLOY_ENABLED` to `true`. Run the workflow
   manually on `main`, or merge the next approved pull request.
6. Check the deployment job and smoke tests, then test an actual login and
   shared SQL data in the browser. CI intentionally has no demo credentials:
   health/static/anonymous-access checks do not by themselves prove SQL access.

Deployment updates only the website package, not App Service settings, SQL,
users, or data. F1 hosting, SQL free-limit auto-pause, and network policy remain
unchanged. Cold starts/free-tier limits and cross-region bandwidth costs remain
applicable.

## Rollback

The workflow retains application ZIP artifacts for 14 days. Keep the last
successful artifact before merging. If a release fails, disable the deployment
gate and redeploy the last known-good complete ZIP using your approved Azure
context. Redeploying an earlier ZIP does not undo database writes.
