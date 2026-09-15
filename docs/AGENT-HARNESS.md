# Jarvis Agent Harness

## Execution lifecycle

Goal -> Planner -> Executor -> Tool Router -> Artifact -> Verification -> Recovery -> Memory

## Verification pipeline

Stages:

- Build
- Test
- Package

Each verification stage produces artifacts for analysis and recovery.

## Autonomous runtime

Runtime coordinates task execution and stores execution snapshots.

## Test coverage

Harness tests cover execution loop completion and retry policy behavior.

## Architecture rule

Core contains orchestration contracts only. OS and runtime integrations are provided through adapters.
