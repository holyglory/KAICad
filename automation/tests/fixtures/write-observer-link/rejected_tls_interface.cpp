// Isolated reproduction of MSVC C2492, never linked into KiCad.
class __declspec(dllexport) REJECTED_OBSERVER
{
    static thread_local REJECTED_OBSERVER* current;
};
thread_local REJECTED_OBSERVER* REJECTED_OBSERVER::current = nullptr;
