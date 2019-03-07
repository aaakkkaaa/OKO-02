using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

public class sTime : MonoBehaviour
{

    // Точное время
    Stopwatch _StopWatch;
    public long StartTime;
    public long UnixStartTime; // стартовое время Unix

    void Awake()
    {
        // Параметры времени
        _StopWatch = new Stopwatch();
        _StopWatch.Start();
        StartTime = _StopWatch.ElapsedMilliseconds;
        UnixStartTime = DateTimeOffset.Now.ToUnixTimeSeconds();
    }

    public int CurrentTime()
    {
        return (int)(_StopWatch.ElapsedMilliseconds - StartTime);
    }


}
