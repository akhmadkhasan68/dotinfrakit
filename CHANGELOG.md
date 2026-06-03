# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.0.0-beta.2] - 2026-06-03

### Added

- `QueueBuilder.PollingInterval(TimeSpan)` — configurable sleep interval between polls when the queue is empty (default 3 s, matching Laravel queue convention)
- `QueueConfiguration.PollingInterval` property

### Changed

- `DatabaseQueueDriver.DequeueAsync` is now single-shot: executes one DB query and returns `null` immediately when no job is found. Polling responsibility has moved to `QueueWorkerService`, which sleeps for `PollingInterval` between calls. This eliminates the hardcoded 100 ms busy-loop that previously generated ~50 DB queries/second at idle.

### Improved

- Database queue idle query rate reduced from ~50 queries/second (hardcoded 100 ms loop) to ~1.7 queries/second at the default 3 s interval

### Docs

- README: added "Polling interval" section under queue configuration
- README: added database polling overhead callout to the EF Core driver section
- README: updated queue monitoring JSON example to include `pollingInterval` field
