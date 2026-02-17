# ECS Compilation Expectations

## Root cause addressed
`ZombieAuthoring` and related ECS gameplay files were wrapped in `#if UNITY_ENTITIES` guards, but `UNITY_ENTITIES` was not defined in this project's compile context. This compiled the full files out, so bakers did not run.

## Expected project setup
- `com.unity.entities` must be present in `Packages/manifest.json`.
- ECS gameplay scripts should compile directly against Entities assemblies (no gameplay-critical `#if UNITY_ENTITIES` wrappers).

## Rule
- Do not wrap required ECS gameplay scripts (`Authoring`, `Baker`, runtime systems/components) in `#if UNITY_ENTITIES`.
- If optional ECS code paths are ever needed in the future, isolate those paths behind separate assemblies or explicit package-based defines, but keep core gameplay authoring/bakers always compiled for ECS-enabled projects.
