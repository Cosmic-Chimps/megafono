# Contract changes: what the app implements

None. The actor lookup and the webhook receiver are unchanged.

- Megáfono stops calling the app's actor lookup for a deleted id; an answer for it would be ignored anyway.
- No webhook is sent for the deletion (the app made the call), and webhooks still queued for the user are dropped.
  No event is queued for a deleted user afterwards, including `delivery.failed` for its `Delete`.
- The vision's `actor.deleted` event stays reserved for remote actors (a later feature).
