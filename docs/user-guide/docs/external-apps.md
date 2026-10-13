# External Apps

External Apps lets the app install and run a small set of curated, separate applications for you, each in its own
containers on your computer. **Today the catalog is empty**, so there is nothing to install yet. This page explains
what the feature is, what you will see, and how installing, updating and removing will work once applications are
offered.

---

## What it is

- **Curated, not open-ended.** Applications come from a catalog the project maintains. You cannot add your own
  container image or Compose file.
- **Runs in containers.** It needs **Docker** installed and running on this computer. Chat, models and every other
  feature work without Docker; only External Apps needs it.
- **Fenced in.** An application gets its own storage folder and is reachable only from this computer. It gets no
  access to your app data, models, conversations or connected devices, and no access to your personal files unless
  it asks for that and you agree.
- **Tested on Linux only.** It has been tried end to end on Linux. Windows and macOS have not been tested yet.

The design decision behind it is [ADR 0010](../../adr/0010-external-apps-container-execution.md).

## Where to find it

External Apps sits in its own **External Apps** group in the navigation, with a **Catalog** and an **Installed**
page. It shows only in the **Advanced** navigation mode (**Node Settings** lets you switch). It is on by default; the
switch is **Node Settings → General → Features → External apps**.

## What you see today

The **Catalog** page has a **Container runtime** card at the top and the list of applications below it.

- **The runtime card** says whether the app can reach Docker. "Ready" means it can. Otherwise it names the problem,
  such as Docker not running or not installed, and **Check again** re-tests after you fix it.
- **The list** says "No applications are available yet." That is expected: the app ships with an empty catalog, and
  the online catalog it will update from does not exist yet.

> **For operators:** a node can be pointed at a catalog document with the `ExternalApps:Catalog:RefreshUrl` setting
> (https, or http to this computer only). It is empty by default, so the app makes no catalog request at all.

## Installing an application

1. On **Catalog**, click **Install** on the application. The runtime card has to show **Ready** first.
2. **Permissions:** read what the application gets and what it does not get, then click **Accept and continue**.
3. **Settings:** fill in what the application needs, such as an admin password. Secret values are stored encrypted
   and never shown again.
4. **Resource check:** the app checks for enough free memory and disk space and stops here if there is not.
5. **Installation:** the app downloads the application and starts it. You can close the window; installing carries
   on.

The application then appears on **Installed**. Click it for its detail page: **Open** it once it is running, **Start**, **Stop** or **Restart** it, change its **Settings** while it is stopped, and read its
**History** and **Logs**.

## Updating

When the catalog offers a newer version, the detail page says so and offers **Update**. The update asks for any new
settings, then shows **Permission changes**: if the new version wants more access than you allowed before, you have
to accept that explicitly with **Accept and update**. A resource check follows, then the update.

## Resetting and uninstalling

- **Reset** deletes everything the application has stored and starts it again from empty. Your settings are kept.
- **Uninstall** removes the application and **permanently deletes everything it has stored**.

Both ask you to confirm first.

**Applications keep running when this app is closed.** They are ordinary Docker containers. Switching the External
Apps feature off hides the pages but does **not** stop anything that is running, so stop or uninstall your
applications first, then switch the feature off.

## The catalog is not signed

The catalog and its applications are not cryptographically signed. What protects you today is that applications
are curated, the catalog is fetched over HTTPS, and every container image is pinned to an exact content hash, so a
changed image is refused. Catalog signing is not built yet.

---

**More detail for developers:** [External Apps in the developer wiki](../../wiki/23-external-apps.md).

**[← Back to the main page](../README.md)**
