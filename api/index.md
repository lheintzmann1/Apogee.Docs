# C# API

The managed engine and editor API, extracted by DocFX from the built `Apogee.CSharp` assembly and
its XML documentation file.

Much of this API is generated from the C++ declarations marked with `API_CLASS`, `API_STRUCT` and
friends, so many types have a [C++ counterpart](../api-cpp/index.md) with the same name and the
same documentation. Editor-only APIs appear here and have no native or Lua equivalent.

## Reading this reference

- Types are grouped by namespace.
- Members hidden from the editor (`[HideInEditor]`) or marked
  `[EditorBrowsable(EditorBrowsableState.Never)]` are excluded.
- The assembly is built in the `Development` configuration for the pinned engine revision.
