# endpoint-sweep

Run all Subsonic REST endpoints against the running plugin and report pass/fail status.

## When to invoke

- User asks to test/verify/validate endpoints, "run the gamut", or "sweep"
- After fixing a bug that affected multiple endpoints
- Before committing a feature that adds or changes endpoint behavior

## Steps

1. Ensure the plugin is deployed and Jellyfin is running:
   ```bash
   ./scripts/deploy-dev.sh   # only if you haven't deployed yet
   ```
2. Run the sweep (auto-resolves credentials from the plugin DB):
   ```bash
   ./scripts/endpoint-sweep.sh
   ```
3. Report the summary line (`N passed, M failed`). If any fail, investigate with:
   ```bash
   BASE=http://localhost:8096/rest
   U=user1; P=$(node scripts/get-creds.js | grep 'u=user1' | sed 's/.*p=\([^ ]*\).*/\1/' | head -1)
   curl -s "$BASE/<failing-method>.view?u=$U&p=$P&v=1.16.1&c=test&f=json" | python3 -m json.tool
   ```

## XML format check

After adding or modifying any endpoint, also verify the XML output uses attributes (not child elements):
```bash
curl -s "http://localhost:8096/rest/<method>.view?u=$U&p=$P&v=1.16.1&c=test" | head -3
# Must show: <element attr="value" />
# Must NOT show: <element><attr>value</attr></element>
```
