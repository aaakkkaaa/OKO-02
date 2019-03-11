using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

public class sWebData : MonoBehaviour
{
    // Желательное время цикла запроса данных, сек.
    [SerializeField]
    float _WebCycleTime = 5.0f;

    // Полный текст запроса к серверу
    public String URL;

    // Время прихода новых данных от сервера.
    public long ResponseTime = 0;

    // Текстовый объект для приема данных от сервера.
    public String ResponseStr = "";

    // Флаг: Имеются новые необработанные данные.
    public bool NewData = false;

    // Параметры времени
    sTime _Time;

    // Объект с методами для записи данных в файлы
    sRecord _Record;

    // Объект FileStream для чтения файла web-данных
    FileStream _RecFile;

    // Start is called before the first frame update
    void Start()
    {
        // Параметры времени
        _Time = transform.GetComponent<sTime>();

        // Ссылка на объект с методами для записи данных в файлы
        _Record = transform.GetComponent<sRecord>();
    }


    // Запросить в Интернете, получить, и записать полетные данные в текстовую строку
    public IEnumerator GetWebData()
    {
        long myWebRequestTime = 0;
        int myWebRequestCount = 0;
        int myDataTraffic = 0;

        yield return new WaitForEndOfFrame();
        _Record.MyLog("RawData", "@@@ GetWebData(): Начну выполнять запросы через ~ 1 секунду");
        yield return new WaitForSeconds(1);
        _Record.MyLog("RawData", "@@@ GetWebData(): Подождали 1 секунду, начинаем");

        while (true)
        {
            myWebRequestTime = _Time.CurrentTime();
            _Record.MyLog("RawData", "@@@ GetWebData(): Начинаю запрос. Время = " + myWebRequestTime + " myURL = " + URL);

            // Готовим запрос
            UnityWebRequest myRequest = UnityWebRequest.Get(URL);
            // Выполняем запрос и получаем ответ
            yield return myRequest.SendWebRequest();
            // Зафиксируем время ответа и интервал времени от предыдущего ответа
            ResponseTime = _Time.CurrentTime(); // Время получения данных от сервера


            myWebRequestTime = ResponseTime - myWebRequestTime; // Время, которое выполняли запрос и получали ответ
            myWebRequestCount++; // Номер запроса

            if (myRequest.isNetworkError || myRequest.isHttpError)
            {
                _Record.MyLog("RawData", "@@@ GetWebData(): Запрос не выполнен. Номер запроса = " + myWebRequestCount + " Время на запрос/ответ = " + myWebRequestTime);
                _Record.MyLog("RawData", "@@@ GetWebData(): Ошибка " + myRequest.error + " Продолжу работать через ~3 секунды");
                yield return new WaitForSeconds(3);
            }
            else
            {
                // Results as text
                ResponseStr = myRequest.downloadHandler.text;
                _Record.WebData("OpenSky", ResponseStr);
                // Установим флаг "Имеются новые необработанные данные"
                NewData = true;
                // Отчитаемся о результатах запроса
                myDataTraffic += ResponseStr.Length;
                _Record.MyLog("RawData", "@@@ GetWebData(): Запрос выполнен. NewData = " + NewData + " Номер запроса = " + myWebRequestCount + " Время прихода ответа = " + ResponseTime + " Время на запрос/ответ = " + myWebRequestTime + " Получена строка длиной = " + ResponseStr.Length + " Общий траффик авиаданных = " + myDataTraffic);
                _Record.MyLog("RawData", "@@@ GetWebData(): " + ResponseStr);
            }

            myRequest.Dispose(); // завершить запрос, освободить ресурсы

            // Переждать до конца рекомендованного времени цикла, секунд (если запрос занял времени меньше)
            float myWaitTime = Mathf.Max(0.0f, (_WebCycleTime - myWebRequestTime / 1000.0f));
            _Record.MyLog("RawData", "@@@ GetWebData(): Переждем до следующего запроса секунд: " + myWaitTime + ".");
            yield return new WaitForSeconds(myWaitTime);
            _Record.MyLog("RawData", "@@@ GetWebData(): Переждали еще секунд: " + myWaitTime + " Буду делать следующий запрос");
        }

    }

    // Получать данные, записанные в файле, выдавать по одной текстовой строке в указанное время
    public IEnumerator GetFileData()
    {
        int FileProcTime = 0;
        int RecordsCount = 0;

        yield return new WaitForEndOfFrame();
        _Record.MyLog("RawData", "@@@ GetFileData(): Начну читать файл ~ 1 секунду");
        yield return new WaitForSeconds(1);
        _Record.MyLog("RawData", "@@@ GetFileData(): Подождали 1 секунду, начинаем");

        // Считать весь файл строку за срокой и записать в массив FileData
        FileProcTime = _Time.CurrentTime();
        string[] RecData = File.ReadAllLines(Path.Combine(_Record.RecDir, "Record.txt"));
        RecordsCount = RecData.Length;
        FileProcTime = _Time.CurrentTime() - FileProcTime;
        _Record.MyLog("RawData", "@@@ GetFileData(): Файл считан. Время чтения: " + FileProcTime + " Всего записей: " + RecordsCount);

        // Разобрать время каждой записи и создать параллельный массив времен (на одну запись больше)
        // Инициализация массива
        int[] RecTime = new int[RecordsCount + 1];
        // Время первой записи
        long.TryParse(RecData[0].Substring(8, 10), out long Rec0UnixTime);
        _Time.UnixStartTime = Rec0UnixTime;
        RecTime[0] = 0;
        // Времена остальных записей
        for (int i = 1; i < RecordsCount; i++)
        {
            long.TryParse(RecData[i].Substring(8, 10), out long RecUnixTime);
            RecTime[i] = (int)(RecUnixTime - Rec0UnixTime) * 1000;
        }
        RecTime[RecordsCount] = RecTime[RecordsCount] + 5000;
        FileProcTime = _Time.CurrentTime() - FileProcTime;
        _Record.MyLog("RawData", "@@@ GetFileData(): Все записи обработаны, создан параллельный массив времен. Время обработки: " + FileProcTime);
        
        // Выдавать по одной строке во время, определенное в массиве времен
        for (int i = 0; i < RecordsCount; i++)
        {
            // Строка - запись данных
            ResponseStr = RecData[i];
            // Время, которому соответствует запись
            ResponseTime = _Time.CurrentTime();

            // Установить флаг "Имеются новые необработанные данные"
            NewData = true;

            _Record.MyLog("RawData", "@@@ GetFileData(): Выдана в обработку запись № " + i + ", время: " + RecTime[i]);

            float myWaitTime = (RecTime[i+1] - _Time.CurrentTime()) / 1000.0f;
            _Record.MyLog("RawData", "@@@ GetFileData(): Переждем до следующей выдачи секунд: " + myWaitTime + ".");
            yield return new WaitForSeconds(myWaitTime);
            _Record.MyLog("RawData", "@@@ GetFileData(): Переждали еще секунд: " + myWaitTime + " Буду делать следующую выдачу");
        }

    }
}
