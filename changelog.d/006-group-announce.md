### Added

- A `Group` actor can announce the replies in its threads, as a Lemmy community does (FEP-1b12): answer
  `announcesReplies: true` for it in the app's actor lookup (Group only). It then boosts every public or unlisted
  reply to its posts, from other servers or published through the app by your other users, to its followers'
  servers and the reply's author; an edit is followed by an announce of the update, a deletion by the retraction of
  the boost. Mastodon and GoToSocial followers see the replies boosted by the Group.
- Announces from a `Group` your users follow are passed on as feed events: the announced post is read from its own
  server and reaches the app as `post.received` (its edit as `post.updated`, the Group's retraction as
  `post.deleted`) with the author in `actor` and the Group in the new `announcedBy` field, `extensions` included.
  One instance of your app can now follow a series account on another instance and receive its threads with their
  comments. Boosts by people are still dropped.

### Changed

- `post.received`, `post.updated` and `post.deleted` carry a new top-level `announcedBy` field: `null` for a post that
  came straight from a followed account.
