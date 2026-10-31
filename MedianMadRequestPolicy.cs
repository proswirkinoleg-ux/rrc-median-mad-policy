using System;
using System.Collections.Generic;
using RRC.Abstractions;

namespace RRC.Student;

public sealed class MedianMadRequestPolicy : IRequestPolicy
{
    private const int WindowSize = 64;
    private const int UpdateInterval = 16;
    private const double MinimumTimeoutMs = 50;
    private const double MaximumTimeoutMs = 2000;
    private readonly Queue<RequestObservation> history = new Queue<RequestObservation>();
    private double timeoutMs = 120;
    private int observationsSinceUpdate = 0;
    private bool limitRetries = false;
    private int retriesInProtection = 0;

    public TimeSpan GetTimeout(RequestContext context, Random random)
    {
        return TimeSpan.FromMilliseconds(timeoutMs);
    }

    public RetryDecision OnResult(RequestObservation observation, Random random)
    {
        // Сохраняем результат. В очереди остаются последние 64 попытки.
        history.Enqueue(observation);
        if (history.Count > WindowSize)
        {
            history.Dequeue();
        }

        int recentTimeouts = CountRecentTimeouts();

        // Пересчитываем таймаут после каждых 16 новых результатов.
        observationsSinceUpdate++;
        if (observationsSinceUpdate >= UpdateInterval)
        {
            observationsSinceUpdate = 0;
            UpdateTimeout(recentTimeouts);
        }

        UpdateRetryProtection();

        // Повтор возможен только после таймаута первой попытки.
        if (observation.Outcome != RequestOutcome.Timeout || observation.Attempt != 1)
        {
            return RetryDecision.NoRetry();
        }

        if (limitRetries)
        {
            retriesInProtection++;

            // Разрешаем первый повтор, следующие три пропускаем, затем снова разрешаем.
            if ((retriesInProtection - 1) % 4 != 0)
            {
                return RetryDecision.NoRetry();
            }
        }

        double delayMs = 100;
        if (history.Count >= UpdateInterval && recentTimeouts >= 8)
        {
            // Если таймаутов много, увеличиваем паузу перед повтором.
            delayMs = 300;
        }

        return RetryDecision.RetryAfter(TimeSpan.FromMilliseconds(delayMs));
    }

    private int CountRecentTimeouts()
    {
        int count = 0;
        int index = 0;
        int startIndex = history.Count - UpdateInterval;

        // Очередь перебирается от старых результатов к новым.
        foreach (RequestObservation result in history)
        {
            if (index >= startIndex && result.Outcome == RequestOutcome.Timeout)
            {
                count++;
            }
            index++;
        }

        return count;
    }

    private void UpdateRetryProtection()
    {
        int longTimeouts = 0;

        foreach (RequestObservation result in history)
        {
            if (result.Outcome == RequestOutcome.Timeout && result.Elapsed.TotalMilliseconds >= 1800)
            {
                longTimeouts++;
            }
        }

        // Много неудач после долгого ожидания: ограничиваем дополнительные попытки.
        // Это признак возможной перегрузки, а не точное определение её причины.
        if (!limitRetries && longTimeouts >= 16)
        {
            limitRetries = true;
            retriesInProtection = 0;
        }
        else if (limitRetries && longTimeouts <= 8)
        {
            // Когда ситуация улучшилась, возвращаем обычные повторы.
            limitRetries = false;
        }
    }

    private void UpdateTimeout(int recentTimeouts)
    {
        List<double> successfulTimes = new List<double>();
        foreach (RequestObservation result in history)
        {
            if (result.Outcome == RequestOutcome.Success)
            {
                successfulTimes.Add(result.Elapsed.TotalMilliseconds);
            }
        }

        // Пока успехов меньше восьми, статистическую оценку не меняем.
        double targetMs = timeoutMs;
        if (successfulTimes.Count >= 8)
        {
            double median = Median(successfulTimes);

            List<double> deviations = new List<double>();
            foreach (double responseTime in successfulTimes)
            {
                double difference = Math.Abs(responseTime - median);
                deviations.Add(difference);
            }

            double mad = Median(deviations);
            targetMs = median + 3 * mad + 10;
        }

        // У таймаутов неизвестно точное время ответа, поэтому считаем их отдельно.
        int timeoutCount = history.Count - successfulTimes.Count;
        double timeoutRate = (double)timeoutCount / history.Count;

        if (timeoutRate > 0.10)
        {
            if (history.Count >= UpdateInterval && recentTimeouts >= 12)
            {
                // При 12 или более таймаутах из последних 16 быстрее увеличиваем ожидание.
                timeoutMs = timeoutMs * 2;
            }
            else
            {
                timeoutMs = timeoutMs * 1.25;
            }
        }
        else if (timeoutRate <= 0.02)
        {
            // Если таймаутов не больше 2%, пробуем уменьшить ожидание на 5%.
            timeoutMs = timeoutMs * 0.95;
        }

        // Не опускаемся ниже оценки по медиане и MAD.
        if (timeoutMs < targetMs)
        {
            timeoutMs = targetMs;
        }

        // Ограничиваем итоговое ожидание: от 50 до 2000 мс.
        if (timeoutMs < MinimumTimeoutMs)
        {
            timeoutMs = MinimumTimeoutMs;
        }
        if (timeoutMs > MaximumTimeoutMs)
        {
            timeoutMs = MaximumTimeoutMs;
        }
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        int middle = values.Count / 2;

        if (values.Count % 2 == 0)
        {
            return (values[middle - 1] + values[middle]) / 2;
        }

        return values[middle];
    }
}
