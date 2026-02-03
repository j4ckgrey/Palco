# Palco

A minimal caching plugin for Jellyfin, built for [Anfiteatro](https://github.com/j4ckgrey/Anfiteatro). Simple key-value storage with TTL support.

## Installation

### Option 1: Plugin Repository (Recommended)

1. Open Jellyfin Dashboard → **Plugins** → **Repositories**
2. Click **Add** and enter:
   - **Name**: `Palco`
   - **URL**: `https://raw.githubusercontent.com/j4ckgrey/Palco/main/manifest.json`
3. Go to **Catalog** tab, find **Palco** under General
4. Click **Install** and restart Jellyfin

### Option 2: Manual Installation

1. Download `palco_0.1.0.0.zip` from the [latest release](https://github.com/j4ckgrey/Palco/releases/latest)
2. Extract to your Jellyfin plugins folder:
   - **Linux**: `/var/lib/jellyfin/plugins/Palco/`
   - **Docker**: `/config/plugins/Palco/`
   - **Windows**: `C:\ProgramData\Jellyfin\Server\plugins\Palco\`
3. Restart Jellyfin

## What it does

Palco provides a simple key-value cache API that Anfiteatro uses to store data on the Jellyfin server:
- Reviews from TMDB/Trakt/IMDB
- API responses that are expensive to fetch
- Any JSON data you want to persist

## API Endpoints

All endpoints require authentication.

### Get Cached Value
```
GET /Palco/Cache/{key}?ns={namespace}
```
Returns `{ key, value, namespace }` or 404 if not found.

### Set Cached Value
```
POST /Palco/Cache/{key}?ns={namespace}
Content-Type: application/json

{
  "value": "{\"your\": \"json data\"}",
  "ttlSeconds": 604800  // 7 days, 0 = never expire
}
```

### Delete Cached Value
```
DELETE /Palco/Cache/{key}?ns={namespace}
```

### Bulk Get
```
POST /Palco/Cache/Bulk?ns={namespace}
Content-Type: application/json

{
  "keys": ["key1", "key2", "key3"]
}
```
Returns `{ "key1": "value1", "key2": "value2" }` (only found keys)

### Delete Namespace
```
DELETE /Palco/Cache/Namespace/{namespace}
```
Deletes all entries in a namespace.

### Clean Expired
```
POST /Palco/Cache/Clean
```
Removes expired entries. Call periodically to clean up.

### Get Stats
```
GET /Palco/Cache/Stats
```
Returns cache statistics.

## Usage Examples

### Caching Reviews
```typescript
// Key format: reviews:{imdbId}
const key = `reviews:${imdbId}`;
const ns = "reviews";

// Check cache first
const cached = await api.get(`/Palco/Cache/${key}?ns=${ns}`);
if (cached) {
  return JSON.parse(cached.value);
}

// Fetch from external API
const reviews = await fetchFromTMDB(imdbId);

// Cache for 7 days
await api.post(`/Palco/Cache/${key}?ns=${ns}`, {
  value: JSON.stringify(reviews),
  ttlSeconds: 604800
});

return reviews;
```

## Building

```bash
cd Palco
dotnet build -c Release
```

Output: `bin/Release/net8.0/Palco.dll`

Copy to your Jellyfin plugins folder and restart Jellyfin.

## License

MIT - Use it however you want.
