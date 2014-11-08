﻿(*
    Copyright 2014 Zumero, LLC

    Licensed under the Apache License, Version 2.0 (the "License");
    you may not use this file except in compliance with the License.
    You may obtain a copy of the License at

        http://www.apache.org/licenses/LICENSE-2.0

    Unless required by applicable law or agreed to in writing, software
    distributed under the License is distributed on an "AS IS" BASIS,
    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
    See the License for the specific language governing permissions and
    limitations under the License.
*)

namespace Zumero.LSM

open System
open System.IO
open System.Collections.Generic

type IPendingSegment = interface end

type IPages =
    abstract member PageSize : int with get
    abstract member Begin : unit->IPendingSegment
    abstract member GetRange : IPendingSegment->int*int
    abstract member End : IPendingSegment*int->Guid

type SeekOp = SEEK_EQ=0 | SEEK_LE=1 | SEEK_GE=2

type ICursor =
    inherit IDisposable
    abstract member Seek : k:byte[] * sop:SeekOp -> unit
    abstract member First : unit -> unit
    abstract member Last : unit -> unit
    abstract member Next : unit -> unit
    abstract member Prev : unit -> unit
    abstract member IsValid : unit -> bool
    abstract member Key : unit -> byte[]
    abstract member Value : unit -> Stream
    abstract member ValueLength : unit -> int
    abstract member KeyCompare : k:byte[] -> int

type ITransaction =
    // TODO consider inherit IDisposable and rollback if commit is never called
    abstract member Commit : seq<Guid> -> unit
    abstract member Rollback : unit->unit

type IDatabaseFile =
    abstract member Exists : unit -> bool
    abstract member OpenForReading : unit -> Stream
    abstract member OpenForWriting : unit -> Stream

type IDatabase = 
    // TODO probably need IDisposable here
    // or a close method
    abstract member WriteSegment : seq<KeyValuePair<byte[],Stream>> -> Guid * int
    abstract member BeginRead : unit->ICursor
    // TODO what happens if it can't get the write lock?  throw?  null?  fs option?  wait?  async?
    abstract member BeginTransaction : unit->ITransaction

module ICursorExtensions =
    let ToSequenceOfKeyValuePairs (csr:ICursor) = seq { csr.First(); while csr.IsValid() do yield new KeyValuePair<byte[],Stream>(csr.Key(), csr.Value()); csr.Next(); done }
    let ToSequenceOfTuples (csr:ICursor) = seq { csr.First(); while csr.IsValid() do yield (csr.Key(), csr.Value()); csr.Next(); done }
