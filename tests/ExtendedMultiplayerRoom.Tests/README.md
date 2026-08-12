# Extended multiplayer room synthetic regression

This test links the real `ExtendedMultiplayerRoomPatches.cs` against minimal
Godot and STS2 API-shape stubs. It does not reference or package commercial game
assemblies or resources.

It verifies that:

- five synchronized treasure relics produce five client holders;
- the fifth player receives a valid default focus target;
- a suppressed treasure result cannot cause a focus index overflow;
- five treasure award hands use distinct edge angles;
- rest sites have one ordered character container per player before vanilla
  indexing, including the larger-grid path;
- the four fixed vanilla rest-site references appended by `_Ready()` are
  removed without disturbing the extended ordering.

Run it from the compat repository root:

```bash
tools/test-extended-multiplayer-rooms.sh
```
