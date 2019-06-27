Unmanaged List
====

[UnmanagedList](../../api/DotNext.Collections.Generic.UnmanagedList-1.yml) is alternative implementation of [List](https://docs.microsoft.com/en-us/dotnet/api/system.collections.generic.list-1) but all elements are located in the unmanaged memory. The list is growable and backed by unmanaged array. This data type is similar to [vector](http://www.cplusplus.com/reference/vector/vector/) class in C++.

_UnmanagedList_ fully implements [IList](https://docs.microsoft.com/en-us/dotnet/api/system.collections.generic.ilist-1) interface so developer can expect the same features as provided by traditional managed list.

The memory during resizing process caused by adding/removing elements are controlled by library, but the lifecycle of the list should be controlled manually.

```csharp
using(var list = new UnmanagedList<long>(10))   //initial capacity
{
    list.Add(10);
    list.Add(20);
    list[0] = 9;
    list.Sort();
}
```

Unmanaged list supports enumeration of elements:
```csharp
using(var list = new UnmanagedList<long>(10))   //initial capacity
{
    list.Add(10);
    list.Add(20);
    foreach(long item in list)
        Console.WriteLine(item);
}
```

It is possible to work with elements of the list using [Span](https://docs.microsoft.com/en-us/dotnet/api/system.span-1) data type with one exception: you should not add, remove or insert new elements when reading or modifying elements through span. These operations may cause to re-allocation of the underlying array. As a result, the original pointer passed into [Span](https://docs.microsoft.com/en-us/dotnet/api/system.span-1) become invalid.
```csharp
using(var list = new UnmanagedList<long>(10))   //initial capacity
{
    list.Add(10);
    list.Add(20);
    Span<long> span = list;
    foreach(ref long item in span)
        item = 0L;
}
```