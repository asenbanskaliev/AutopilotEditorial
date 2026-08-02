# RED evidence — VS-126

Before implementation, installation was `PARTIAL`: repository build/CI existed but there was no signed package verification, guided first run, durable resume, protected deployment credential configuration, bounded installer repair or exact installation evidence.

Required failing behaviors selected:
1. Reject digest mismatch.
2. Reject invalid or absent Authenticode signature.
3. Reject path escape.
4. Preserve and resume phase state after interruption.
5. Reject missing provider, secret or invalid cost limit.
6. Stop automatic repair after the configured ceiling.
7. Avoid repeating a completed setup.
