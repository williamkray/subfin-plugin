# Subfin — Subsonic plugin for Jellyfin

Exposes an [OpenSubsonic](https://opensubsonic.netlify.app/)-compatible REST API directly from Jellyfin. Subsonic and Navidrome clients connect to your existing Jellyfin server — no separate process, no proxy.

## Requirements

- Jellyfin **10.11.x** (built against 10.11.6)
- .NET 9 runtime (included in Jellyfin 10.11.x)

## Installation

1. In Jellyfin, go to **Dashboard → Plugins → Repositories** and add:
   ```
   https://raw.githubusercontent.com/williamkray/subfin-plugin/main/jellyfin-plugin-subfin-manifest.json
   ```
2. Go to **Catalog**, find **Subfin**, and install it.
3. Restart Jellyfin.

## Client setup

Each Subsonic client needs its own app password. This is separate from your Jellyfin password.

1. Open **`http://<your-jellyfin>/subfin/`** in a browser (you must be logged in to Jellyfin).
2. Optionally enter a label (e.g. "DSub on Phone"), then click **Link Device**.
3. Copy the generated **username** and **password** — the password won't be shown again.
4. In your Subsonic client, configure the server:
   - **Server URL:** `http://<your-jellyfin>`
   - **Username / Password:** from step 3

To revoke a client's access, return to the device manager and click **Unlink**.

## Admin features

Admin settings are in **Dashboard → Plugins → Subfin** (the Jellyfin plugin config page).

### Last.fm

Enter a Last.fm API key to enable artist biographies and images in clients that request them (`getArtistInfo`, `getArtistInfo2`). Leave blank to skip that data.

### Sharing

Any user can create shareable links via their Subsonic client (`createShare`). Shares are accessible at `/subfin/share/<uid>` — no Jellyfin login required.

**Managing shares** — the device manager at `/subfin/` shows:
- Your own shares (all users)
- All shares across all users (Jellyfin admins only)

Admins can delete any share from that page. Non-admins can only delete their own.

### Library selection

Each user can choose which Jellyfin music libraries are visible through Subsonic clients. Open the device manager and use the **Library Selection** section. Deselecting all libraries shows everything (no restriction).
