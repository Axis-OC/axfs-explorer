using System;
using System.Collections.Generic;

namespace AxfsExplorer.Helpers;

enum UndoActionType
{
    DeleteFile,
    DeleteDir,
    Rename,
    ModifyFile,
}

record UndoAction(UndoActionType Type, string Path, byte[]? OldData = null, string? AltPath = null);

class UndoManager
{
    readonly Stack<UndoAction> _undo = new();
    readonly Stack<UndoAction> _redo = new();
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public int UndoCount => _undo.Count;

    public void Record(UndoAction a)
    {
        _undo.Push(a);
        _redo.Clear();
    }

    public UndoAction? PopUndo() => _undo.Count > 0 ? _undo.Pop() : null;

    public UndoAction? PopRedo() => _redo.Count > 0 ? _redo.Pop() : null;

    public void PushRedo(UndoAction a) => _redo.Push(a);

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }
}
