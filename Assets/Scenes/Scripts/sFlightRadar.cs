using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Text;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;
using Newtonsoft.Json.Linq;
using Mapbox.Unity.Utilities;
using Mapbox.Utils;

// Выполняет запрос к серверу adsbexchange.com, получает, обрабатывает и сохраняет данные полетов в массивы структур.


public class sFlightRadar : MonoBehaviour {

    // Корневая часть запроса к ADS-B Exchange
    String REQUEST_BASE_URL = "http://public-api.adsbexchange.com/VirtualRadar/AircraftList.json?";

    // Корневая часть запроса к OpenSky
    String REQUEST_OpenSky_BASE_URL = "https://aka2001:MyOpenSky@opensky-network.org/api/states/all?";

    // Полный текст запроса к серверу
    //String myURL;

    // Координаты начальной точки
    //[SerializeField]
    //String myStartLatitude = "55.97239722";
    //[SerializeField]
    //String myStartLongitude = "37.41305278";

    // Высота аэропорта над уровнем моря
    [SerializeField]
    float myAirport_ALt = 192;
    // Расстояние от начальной точки (км), на котором следить за самолетами
    [SerializeField]
    float myDistance = 60.0f;
    // Один фут, метров
    [SerializeField]
    float myFeet = 0.3048f;
    // Узел, метров в секунду (1.852 км/час)
    [SerializeField]
    float myKnot = 0.514f;
    // Метрическая система, используемая по умолчанию (true - СИ, false - Футы/мили)
    [SerializeField]
    bool mySI = true;
    // Отставание отображения от жизни, миллисек. (самолет перемещается так, чтобы оказаться в последней точке через данное время
    [SerializeField]
    int myLag = 20000;
    // Желательное время цикла обработки данных, сек.
    [SerializeField]
    float myProcCycleTime = 1.0f;
    // Текстовый объект для вывода сообщений на экран
    [SerializeField]
    Text mySceenMessage;
    // Баннер № 2 для вывода доп. информации о самолете
    [SerializeField]
    Transform myBanner2;
    // Контроллер карты MapBox. Для работы с высотой в Иннсбруке
    [SerializeField]
    GameObject myMap;
    // Квадрат минимальной допустимой скорости движения самолета. Базовая величина соответствует 20 м/сек = 72 км/час (39,45 узла)
    [SerializeField]
    float myLowSpeedSqr = 400.0f;

    // Уникальный номер самолета в текущей сессии работы программы.
    int myPlaneNumber = 0;

    // Объект для вывода сообщений в пространство
    sTextMessage myWorldMessage;

    // Массив объектов - пороги ВПП
    //[SerializeField]
    //GameObject[] myTHR = new GameObject[4];

    // Трансформ шаблона самолетов
    Transform mySamplePlane;
    // Трансформ группового объекта самолетов
    Transform myPlanesController;
    // Трансформы шаблонов путевых точек
    Transform[] myTrackPoint = new Transform[5];

    // Данные, которые будет определять MapBox (исправления в коде Sc02MapAtWorldScaleAndSpecificLocation.cs)
    [HideInInspector]
    public float myWorldRelativeScale;
    [HideInInspector]
    public Vector2d myCenterMercator;
    [HideInInspector]
    public Vector3 myPosShift;
    // Координаты начальной точки
    [HideInInspector]
    public String myStartLatitude;
    [HideInInspector]
    public String myStartLongitude;


    // Время

    // long myResponseTime = 0; // Время прихода новых данных от сервера. Переехало в WebData
    long myStartProcTime = 0; // Время начала первичной обработки данных (если новые данные поступили, то совпадает с _WebData.ResponseTime)
    long myStartProcTime2 = 0; // Время начала обработки данных (если новые данные не поступили и первичная обработка не выполнялась, т.е. пока прогноз не строили)
    long myLastDeltaTime = 0; // Время выполнения последнего полного цикла обработки данных (первичной + вторичной)
    int myPosDelay = 0; // разница между исходным временем позиционирования (time_position) и временем на которое идет расчет положения (myStartProcTime)

    // Текстовый объект для приема данных от сервера. Переехал в WebData
    //String myResponseStr = "";

    // Поток, в котором будем обрабатывать полученные данные и поддерживать структуры
    Thread myFightDataThread;

    // Флаги разных состояний
    //bool myNewWebData = false; // Имеются новые необработанные данные. Переехал в WebData
    bool myPrimaryDataProc = true; // Выполняется первичная обработка данных (в фоновом потоке), вторичную обработку не начинать!
    bool mySecondaryDataProc = false; // Выполняется вторичная обработка данных (в корутине), первичную обработку не начинать!
    bool myBanner1AddInfo = false; // Выводить ли на баннер с краткой информацией дополнительную информацию
    [SerializeField]
    bool _DataFromWeb = true; // Получать данные из интернета. В противном случае из файла Record.txt


    // ******************************************************************
    
    // Структура (на самом деле класс) для текущих параметров самолета, полученных от opensky-network.org ("Большая")
    // Пришлось переделать в класс, так как экземпляры System.Reflection.FieldInfo не отрабатывают метод setValue для структур.
    class myPlaneParameters_OpenSky
    {
        // Список полей отсюда: https://opensky-network.org/apidoc/rest.html#response

        public String icao24; // Unique ICAO 24-bit address of the transponder in hex string representation.
        public String callsign; // Callsign of the vehicle (8 chars). Can be null if no callsign has been received.
        public String origin_country; // Country name inferred from the ICAO 24-bit address.
        public long time_position; // Unix timestamp (seconds) for the last position update. Can be null if no position report was received by OpenSky within the past 15s.
        public long last_contact; // Unix timestamp (seconds) for the last update in general. This field is updated for any new, valid message received from the transponder.
        public float longitude; // WGS-84 longitude in decimal degrees. Can be null.
        public float latitude; // WGS-84 latitude in decimal degrees. Can be null.
        public float baro_altitude; // Barometric altitude in meters. Can be null.
        public bool on_ground; // Boolean value which indicates if the position was retrieved from a surface position report.
        public float velocity; // Velocity over ground in m/s. Can be null.
        public float true_track; // True track in decimal degrees clockwise from north (north=0°). Can be null.
        public float vertical_rate; // Vertical rate in m/s. A positive value indicates that the airplane is climbing, a negative value indicates that it descends. Can be null.
        public int[] sensors; // IDs of the receivers which contributed to this state vector. Is null if no filtering for sensor was used in the request.
        public float geo_altitude; // Geometric altitude in meters. Can be null.
        public String squawk; // The transponder code aka Squawk. Can be null.
        public bool spi; // Whether flight status indicates special purpose indicator.
        public int position_source; // Origin of this state’s position: 0 = ADS-B, 1 = ASTERIX, 2 = MLAT

        public String myNumber; // Уникальный номер самолета в текущей сессии работы программы. Присваиваю сам.
        public String myModel; //
        public String myOperator; //
        public String myFrom; //
        public String myTo; //

    }

    /*
    // Структура (на самом деле класс) для текущих параметров самолета, полученных от adsbexchange.com ("Большая")
    // Пришлось переделать в класс, так как экземпляры System.Reflection.FieldInfo не отрабатывают метод setValue для структур.
    class myPlaneParameters
    {
        // Список полей отсюда: http://www.virtualradarserver.co.uk/Documentation/Formats/AircraftList.aspx
        // Дополнительная информация: https://www.adsbexchange.com/datafields/

        public int Id; // The Unique Identifier of the aircraft (in current tracking session?). Virtual Radar Server
        public int TSecs; // The number of seconds that the aircraft has been tracked for. Virtual Radar Server
        public int Rcvr; // The ID of the feed that last supplied information about the aircraft. Virtual Radar Server
        public String Icao; // The ICAO hex identifier of the aircraft. Broadcast
        public bool Bad; // True if the ICAO is known to be invalid. This information comes from the local BaseStation.sqb database.
        public String Reg; // The aircraft registration number
        public DateTime FSeen; // Date and time the receiver first started seeing the aircraft on this flight (datetime – epoch format).
        public int Alt; // The altitude in feet at standard pressure. Broadcast
        public int GAlt; // The altitude adjusted for local air pressure, should be roughly the height above mean sea level.
        public float InHg; // The air pressure in inches of mercury that was used to calculate the AMSL altitude from the standard pressure altitude.
        public int AltT; // The type of altitude transmitted by the aircraft: 0= standard pressure altitude, 1= indicated altitude (above mean sea level). Default to standard pressure altitude until told otherwise.
        public int TAlt; // The target altitude, in feet, set on the autopilot / FMS etc. Broadcast
        public String Call; // The callsign. Broadcast
        public bool CallSus; // True if the callsign may not be correct. Based on a checksum of the data received over the air.
        public float Lat; // The aircraft’s latitude over the ground. Broadcast
        public float Long; // The aircraft’s longitude over the ground. Broadcast
        public long PosTime; // The time (at UTC in JavaScript ticks) that the position was last reported by the aircraft.
        public bool Mlat; // True if the latitude and longitude appear to have been calculated by an MLAT server and were not transmitted by the aircraft.
        public bool PosStale; // True if the last position update is older than the display timeout value - usually only seen on MLAT aircraft in merged feeds. Internal field, basically means that the position data is > 60 seconds old (unless it’s from Satellite ACARS).
        public bool Tisb; // True if the last message received for the aircraft was from a TIS-B source.
        public float Spd; // The ground speed in knots. Broadcast
        public int SpdTyp; // The type of speed that Spd represents. Only used with raw feeds. 0/missing= ground speed, 1= ground speed reversing, 2= indicated air speed, 3= true air speed.
        public int Vsi; // Vertical speed in feet per minute. Broadcast
        public int VsiT; // 0= vertical speed is barometric, 1= vertical speed is geometric. Default to barometric until told otherwise.
        public float Trak; // Aircraft's track angle across the ground clockwise from 0° north. Broadcast(?).
        public bool TrkH; // True if Trak is the aircraft's heading, false if it's the ground track. Default to ground track until told otherwise.
        public float TTrk; // The track or heading currently set on the aircraft's autopilot or FMS. Broadcast
        public String Type; // The aircraft model's ICAO type code.
        public String Mdl; // A description of the aircraft's model.
        public String Man; // The manufacturer's name.
        public String CNum; // The aircraft's construction or serial number.
        public String From; // The code and name of the departure airport.
        public String To; // The code and name of the arrival airport.
        public String Stops; // An array of strings, each being a stopover on the route.
        public String Op; // The name of the aircraft's operator.
        public String OpIcao; // The operator's ICAO code.
        public String Sqk; // Transponder squawk code. This is a 4-digit code (each digit is from 0-7) entered by the pilot, and typically assigned by air traffic control. A sqwak code of 1200 typically means the aircraft is operation under VFR and not receiving radar services. 7500= Hijack code, 7600= Lost Communications, radio problem, 7700= Emergency.
        public bool Help; // True if the aircraft is transmitting an emergency squawk.
        public float Dst; // The distance to the aircraft in kilometres.
        public float Brng; // The bearing from the browser to the aircraft clockwise from 0° north.
        public int WTC; // The wake turbulence category (0= None, 1= Light, 2= Medium, 3= Heavy). Broadcast
        public String Engines; // The number of engines the aircraft has. Usually '1', '2' etc. but can also be a string - see ICAO documentation.
        public int EngType; // Type of engine the aircraft uses (0= None, 1= Piston, 2= Turboprop, 3= Jet, 4= Electric).
        public int EngMount; // The placement of engines on the aircraft (0= Unknown, 1= Aft Mounted, 2= Wing Buried, 3= Fuselage Buried, 4 =Nose Mounted, 5= Wing Mounted).
        public int Species; // General Aircraft Type (0 =None, 1 =Land Plane, 2= Sea Plane, 3= Amphibian, 4= Helicopter, 5= Gyrocopter, 6= Tiltwing, 7= Ground Vehicle, 8= Tower).
        public bool Mil; // True if the aircraft appears to be operated by the military.
        public String Cou; // The country that the aircraft is registered to.
        public bool HasPic; // True if the aircraft has a picture associated with it in the VRS/ADSBexchange database. Pictures often link to http://www.airport-data.com.
        public int PicX; // The width of the picture in pixels.
        public int PicY; // The height of the picture in pixels.
        public int FlightsCount; // The number of Flights records the aircraft has in the database.
        public int CMsgs; // The count of messages received for the aircraft. Will change as aircraft roams between receiving servers.
        public bool Gnd; // True if the aircraft is on the ground. Broadcast
        public String Tag; // The user tag found for the aircraft in the BaseStation.sqb local database.
        public bool Interested; // True if the aircraft is flagged as interesting in the BaseStation.sqb local database.
        public String TT; // Trail type - empty for plain trails, 'a' for trails that include altitude, 's' for trails that include speed.
        public int Trt; // Transponder type - 0=Unknown, 1=Mode-S, 2=ADS-B (unknown version), 3=ADS-B 0, 4=ADS-B 1, 5=ADS-B 2.
        public int Year; // The year that the aircraft was manufactured.
        public bool Sat; // True if the aircraft has been seen on a SatCom ACARS feed (e.g. a JAERO feed).
        public int[] Cos; // Short trails.
        public int[] Cot; // Full trails.
        public bool ResetTrail; // True if the entire trail has been sent and the JavaScript should discard any existing trail history it's built up for the aircraft.
        public bool HasSig; // True if the aircraft has a signal level associated with it.
        public int Sig; // The signal level for the last message received from the aircraft, as reported by the receiver. Not all receivers pass signal levels. The value's units are receiver-dependent.
    }
    */

    // Словарь - массив параметров всех самолетов, полученных от adsbexchange. Ключ - HEX код ICAO (Icao), значение - структура со всеми данными
    // Если поле Icao пустое, используем для ключа ID, выдаваемый виртуальным радар-сервером
    Dictionary<String, myPlaneParameters_OpenSky> myAllPlanesPars = new Dictionary<String, myPlaneParameters_OpenSky>();
    // Коллекция ключей словаря
    Dictionary<String, myPlaneParameters_OpenSky>.KeyCollection myAllPlanesKeysList;

    // Словарь - массив для описания типов полей большой структуры. Ключ - имя поля, значение - тип поля
    Dictionary<String, Type> myPlaneParsTypeOpenSky = new Dictionary<String, Type>();
    // Коллекция значений словаря
    Dictionary<String, Type>.KeyCollection myPlaneParsTypeOpenSky_Keys;


    // Словарь - массив для описания типов полей большой структуры. Ключ - имя поля, значение - тип поля
    Dictionary<String, Type> myPlaneParsType = new Dictionary<String, Type>();


    // Структура для отображения самолета ("Малая")
    struct MyPlaneVisual
    {
        public String Key;
        public String Call;
        public String Icao;
        //public String Reg;
        public String Alt;
        public GameObject GO;
        public Transform Banner1;
        public Text Banner1Call;
        public Text Banner1Icao;
        public Text Banner1PReason;
        public Text Banner1Model;
        public Text Banner1Alt;
        public Image Banner1Panel;
        public Transform Model;
        public String PredictionReason;
        public int BadPosCounter;
        public Vector3 RawPosition;
        public Vector3 Position;
        public Vector3 Euler;
        public Vector3 Speed;
        public long Time;
        public bool NeedNewBezier;
        public Vector3 StartBezier;
        public Transform[] TrackPoints;
        public long TrackTime; // Время полета по путевым точкам
        public long TargetTime; // Время прихода в конечную целевую точку
        public long PrevTargetTime; // Время прихода в конечную целевую точку для предыдущего цикла обработки
    }

    // Словарь - данные для отображения самолетов. Ключ - HEX код ICAO, значения - структура
    Dictionary<String, MyPlaneVisual> myPlaneVis = new Dictionary<String, MyPlaneVisual>();
    // Коллекция значений словаря
    Dictionary<String, MyPlaneVisual>.ValueCollection myPlaneVisValues;
    // Коллекция ключей словаря
    Dictionary<String, MyPlaneVisual>.KeyCollection myPlaneVisKeys;

    // Структура массивов для истории полетов ("История")
    struct MyFlightHistory
    {
        public List<long> Time;
        public List<long> PosTime;
        public List<String> PredictionReason;
        public List<int> BadPosCounter;
        public List<Vector3> RawPosition;
        public List<Vector3> Position;
        public List<Vector3> Euler;
        public List<Vector3> Speed;
    }
    // Словарь - истории полетов. Ключ - HEX код ICAO, значения - структура массивов List с временем, координатами и скоростями
    Dictionary<String, MyFlightHistory> myPlanesHistory = new Dictionary<String, MyFlightHistory>();

    // Словарь - список севших самолетов. Нужен, чтобы не создавать такой заново, если он уже удален, а данные еще поступают
    Dictionary<String, long> myLandedPlanes = new Dictionary<String, long>();

    // Словарь - список самолетов со слишком малой скоростью. Нужен, чтобы не отображать "зависшие" самолеты
    Dictionary<String, bool> mySlowPlanes = new Dictionary<String, bool>();
    // Счетчик медленных самолетов
    int mySlowPlanesCount = 0;

    // Словарь - список имеющихся моделей самолетов. Содержит указатели на модели. Ключи - имена на сцене. Нужен для быстрого доступа к моделям при создании новых самолетов
    Dictionary<String, Transform> myPlanes3D = new Dictionary<String, Transform>();

    // Словарь "Известные модели самолетов -> имеющиеся 3D модели". Ключ - "Код ИКАО модели", содержание - первая часть имени модели на сцене (без авиакомпании)
    Dictionary<String, String> myKnownPlanes = new Dictionary<String, String>();

    // Словарь "Известные авиакомпании -> имеющиеся авиакомпании (среди 3D моделей)". Ключ - "Код ИКАО авиакомпании", содержание - вторая часть имени модели на сцене (без модели самолета)
    Dictionary<String, String> myKnownAirlines = new Dictionary<String, String>();

    // Словарь - список полей баннера с дополнительной информацией
    Dictionary<String, Text> myBanner2Fields = new Dictionary<String, Text>();

    // Самолет, выбранный для отображения дополнительной информации (ключ - HEX код ICAO или ID от Virtual Radar Server)
    String mySelectedPlane = null;

    // Параметры времени
    sTime _Time;

    // Объект с методами для записи данных в файлы
    sRecord _Record;

    // Объект для получения исходных данных
    sWebData _WebData;

    // Use this for initialization
    void Start () {

        // Параметры времени
        _Time = transform.GetComponent<sTime>();
        print("StartTime = " + _Time.StartTime + " UnixStartTime = " + _Time.UnixStartTime);

        // Ссылка на объект с методами для записи данных в файлы
        _Record = transform.GetComponent<sRecord>();

        // Объект для получения исходных данных
        _WebData = transform.GetComponent<sWebData>();


        // Полный текст запроса к серверу

        // ADS-B Exchange
        //myURL = REQUEST_BASE_URL + "lat=" + myStartLatitude + "&lng=" + myStartLongitude + "&fDstL=0&fDstU=" + myDistance;

        // OpenSky
        print("Контрольная точка аэродрома: " + myStartLatitude + "," + myStartLongitude);
        // Границы прямоугольника +/-(myDistance/2)
        float myLat;
        float.TryParse(myStartLatitude, out myLat);
        float myLong;
        float.TryParse(myStartLongitude, out myLong);
        float myLatHalfDistance = myDistance / 111.111f; //111.111 - длина одного градуса меридиана в км
        float myLongHalfDistance = myDistance / Mathf.Cos(myLat*Mathf.Deg2Rad) / 111.111f;
        float myWestMargin = myLat - myLatHalfDistance;
        float myEastMargin = myLat + myLatHalfDistance;
        float myNorthMargin = myLong + myLongHalfDistance;
        float mySouthMargin = myLong - myLongHalfDistance;

        _WebData.URL = REQUEST_OpenSky_BASE_URL + "lamin=" + myWestMargin + "&lomin=" + mySouthMargin + "&lamax=" + myEastMargin + "&lomax=" + myNorthMargin;

        print("myURL = " + _WebData.URL);


        // Трансформ шаблона самолетов - получить указатель и сразу спрятать
        mySamplePlane = GameObject.Find("SamplePlane").transform;
        mySamplePlane.gameObject.SetActive(false);

        // Трансформы группы путевых точек - получить указатели и сразу спрятать
        for (int i = 0; i < 5; i++)
        {
            myTrackPoint[i] = GameObject.Find("TraсkPointers/Point_" + i).transform;
            myTrackPoint[i].gameObject.SetActive(false);
        }

        // Трансформ группового объекта самолетов
        myPlanesController = GameObject.Find("PlanesController").transform;

        // Коллекция значений словаря
        myPlaneVisValues = myPlaneVis.Values; // малый
        // Коллекция ключей словарей
        myPlaneVisKeys = myPlaneVis.Keys; // малый

        // Заполним словарь - список имеющихся моделей самолетов
        Transform myObjTr = GameObject.Find("Planes3D").transform;
        for(int i=0; i < myObjTr.childCount; i++)
        {
            Transform myPlaneTr = myObjTr.GetChild(i);
            myPlanes3D.Add(myPlaneTr.name, myPlaneTr);
        }

        // Заполним словарь "Известные модели самолетов -> имеющиеся 3D модели". Ключ - "Код ИКАО модели", содержание - первая часть имени модели на сцене (без авиакомпании)
        myKnownPlanes.Add("A319", "A320");
        myKnownPlanes.Add("A320", "A320");
        myKnownPlanes.Add("A321", "A320");

        myKnownPlanes.Add("A332", "A330");
        myKnownPlanes.Add("A333", "A330");

        myKnownPlanes.Add("A388", "A380");

        myKnownPlanes.Add("B731", "B737");
        myKnownPlanes.Add("B732", "B737");
        myKnownPlanes.Add("B733", "B737");
        myKnownPlanes.Add("B734", "B737");
        myKnownPlanes.Add("B735", "B737");
        myKnownPlanes.Add("B736", "B737");
        myKnownPlanes.Add("B737", "B737");
        myKnownPlanes.Add("B738", "B737");
        myKnownPlanes.Add("B739", "B737");

        myKnownPlanes.Add("B741", "B747");
        myKnownPlanes.Add("B742", "B747");
        myKnownPlanes.Add("B743", "B747");
        myKnownPlanes.Add("B744", "B747");
        myKnownPlanes.Add("B748", "B747");
        myKnownPlanes.Add("B74D", "B747");
        myKnownPlanes.Add("B74R", "B747");
        myKnownPlanes.Add("B74S", "B747");

        myKnownPlanes.Add("B772", "B777");
        myKnownPlanes.Add("B773", "B777");

        myKnownPlanes.Add("E135", "ERJ145");
        myKnownPlanes.Add("E145", "ERJ145");

        myKnownPlanes.Add("E170", "E170");
        myKnownPlanes.Add("IL96", "IL96");
        myKnownPlanes.Add("SU95", "SSJ100");

        myKnownPlanes.Add("C172", "Cessna172");
        myKnownPlanes.Add("C72R", "Cessna172");

        // Заполним словарь "Известные авиакомпании -> имеющиеся авиакомпании". Ключ - "Код ИКАО авиакомпании", содержание - вторая часть имени модели на сцене (без модели самолета)
        myKnownAirlines.Add("AFL", "AFL");
        myKnownAirlines.Add("SBI", "S7");


        // Заполним словарь - список полей баннера с дополнительной информацией
        // Для всех детей объекта myBanner2
        for (int i = 0; i < myBanner2.childCount; i++)
        {
            Transform myChildTr = myBanner2.GetChild(i);
            String myChildName = myChildTr.name;
            if(myChildName != "Panel") // Исключение: объект "Panel" - это не текстовое поле
            {
                myBanner2Fields.Add(myChildName, myChildTr.GetComponent<Text>());
                //print(myChildName + " = " + myBanner2Fields[myChildName]);
            }
        }

        // Словарь для описания типов полей большой структуры. Ключ - имя поля, значение - тип поля

        myPlaneParameters_OpenSky myPP_OpenSky = new myPlaneParameters_OpenSky(); // Создадим пустой экземпляр большой структуры. Тестируя содержимое его полей узнаем их типы
        Type myPP_OpenSky_Type = typeof(myPlaneParameters_OpenSky); // тип объекта "myPlaneParameters"
        System.Reflection.MemberInfo[] memberlist_OpenSky = myPP_OpenSky_Type.GetMembers(); // Получим список членов данного типа

        // Пройдем по всем полям большой структуры
        for (int i = 0; i < memberlist_OpenSky.Length; i++)
        {
            // Только для членов, которые являются полями
            if (memberlist_OpenSky[i].MemberType == System.Reflection.MemberTypes.Field)
            {
                String myName = memberlist_OpenSky[i].Name; // имя поля
                System.Reflection.FieldInfo myFieldInfo = myPP_OpenSky_Type.GetField(myName); // метаинформация поля
                Type myType; // тип поля
                // Попробуем получить тип из содержимого поля
                try
                {
                    myType = myFieldInfo.GetValue(myPP_OpenSky).GetType();
                }
                catch // В пустом экземпляре строки и массивы не присвоены и выдают ошибку
                {
                    //_Record.MyLog(myCount + ", Ошибка: " + myEx.Message + ", Name = " + myName);
                    if (myName == "sensors") // известно, что это поле - массив int
                    {
                        myType = typeof(int[]);
                    }
                    else // все остальные поля - String
                    {
                        myType = typeof(String);
                    }
                }
                myPlaneParsTypeOpenSky.Add(myName, myType);
            }
        }

        /*
        // Словарь для описания типов полей большой структуры. Ключ - имя поля, значение - тип поля

        myPlaneParameters myPP = new myPlaneParameters(); // Создадим пустой экземпляр большой структуры. Тестируя содержимое его полей узнаем их типы
        Type myPPType = typeof(myPlaneParameters); // тип объекта "myPlaneParameters"
        System.Reflection.MemberInfo[] memberlist = myPPType.GetMembers(); // Получим список членов данного типа

        // Пройдем по всем полям большой структуры
        for (int i = 0; i < memberlist.Length; i++)
        {
            // Только для членов, которые являются полями
            if (memberlist[i].MemberType == System.Reflection.MemberTypes.Field)
            {
                String myName = memberlist[i].Name; // имя поля
                System.Reflection.FieldInfo myFieldInfo = myPPType.GetField(myName); // метаинформация поля
                Type myType; // тип поля
                // Попробуем получить тип из содержимого поля
                try
                {
                    myType = myFieldInfo.GetValue(myPP).GetType();
                }
                catch // В пустом экземпляре строки и массивы не присвоены и выдают ошибку
                {
                    //_Record.MyLog(myCount + ", Ошибка: " + myEx.Message + ", Name = " + myName);
                    if(myName == "Cos" || myName == "Cot") // известно, что эти поля - массивы int
                    {
                        myType = typeof(int[]);
                    }
                    else // все остальные поля - String
                    {
                        myType = typeof(String);
                    }
                }
                myPlaneParsType.Add(myName, myType);
                print("Поле: " + myName + ", Тип: " + myType);
            }
        }
        */

        // Объект для вывода сообщений в пространство
        myWorldMessage = GameObject.Find("TextMessage").GetComponent<sTextMessage>();

        // Спрячем все имеющиеся образцовые модели
        myObjTr.gameObject.SetActive(false);

        // Запуск корутины получения данных
        if (_DataFromWeb)
        {
            StartCoroutine(_WebData.GetWebData()); // web данные от сервера
        }
        else
        {
            StartCoroutine(_WebData.GetFileData()); // данные, сохраненные в файле Record.txt
        }

        // Запуск фонового потока первичной обработки данных
        myFightDataThread = new Thread(new ThreadStart(myFuncThread)) { IsBackground = true }; // Создаем поток из функции myFuncThread()

        // Объявляем о начале первичной обработки данных в потоке (На самом деле, сначала поток еще будет ждать первого получения данных). Нужно для блокировки вторичной обработки
        myPrimaryDataProc = true;
        mySecondaryDataProc = false;

        // Запускаем поток
        myFightDataThread.Start();

        // Запуск корутины вторичной обработки данных
        StartCoroutine(myFuncProcData());

        // Для работы с высотой в Иннсбруке
        if(myAirport_ALt == 581.0f)
        {
            StartCoroutine(myFuncInnsbrukSpecial());
        }
    }

    IEnumerator myFuncInnsbrukSpecial()
    {
        yield return new WaitForEndOfFrame();
        Vector3 myPos = myMap.transform.position;
        myPos.y = -myAirport_ALt;
        myMap.transform.position = myPos;
    }


    // Первичная обработка данных, выполняемая в фоновом потоке. Сюда, по возможности, вынесены операции из корутины myFuncProcData()
    void myFuncThread()
    {

        // Ждем получения новых данных в корутине _WebData.GetWebData(). Выполняется только в начале работы программы
        _Record.MyLog("RawData", "ProcData", "=== myFuncThread(): Ждем первого получения новых данных. NewData = " + _WebData.NewData);
        while (!_WebData.NewData)
        {
            Thread.Sleep(200);
        }
        bool myFirstData = true; // Признак первого получения данных

        // Бесконечный цикл первичной обработки данных
        while (true)
        {

            if (myFirstData)
            {
                _Record.MyLog("RawData", "ProcData", "=== myFuncThread(): Обнаружено первое получение новых данных. NewData = " + _WebData.NewData);
                myFirstData = false;
            }
            else
            {
                // Ждем заверешения вторичной обработки данных в корутине myFuncProcData()
                _Record.MyLog("RawData", "ProcData", "=== myFuncThread(): Ждем заверешения вторичной обработки данных в корутине myFuncProcData()");
                _Record.MyLog("RawData", "ProcData", "--------------------------------------------------------");
                while (mySecondaryDataProc)
                {
                    Thread.Sleep(20);
                }
                _Record.MyLog("RawData", "ProcData", "=== myFuncThread(): Обнаружено, что вторичная обработка данных завершена.");
            }
            _Record.MyLog("RawData", "ProcData", "=== myFuncThread(): Начинаем первичную обработку данных");

            // Начинаем первичную обработку данных
            // Если получены новые данные в корутине _WebData.GetData(), то начинаем их разбирать
            if (_WebData.NewData)
            {
                _Record.MyLog("RawData", "ProcData", "=== myFuncThread(): Есть новые данные от _WebData.GetWebData(). NewData = " + _WebData.NewData);
                _WebData.NewData = false;
                myLastDeltaTime = _WebData.ResponseTime - myStartProcTime; // Время работы последнего выполненного полного цикла обработки данных
                myStartProcTime = _WebData.ResponseTime; // Время начала нового полного цикла обработки данных
                myStartProcTime2 = myStartProcTime;

                _Record.MyLog("RawData", "ProcData", "=== myFuncThread(): Время начала нового цикла обработки считаем от _WebData.ResponseTime = myStartProcTime = " + myStartProcTime + " myLastDeltaTime = " + myLastDeltaTime);
                // Парсим полученную строку и создаем объект JSON
                dynamic myJObj = JObject.Parse(_WebData.ResponseStr);
                // Узел "states" - массив состояний самолетов (статических векторов)
                JArray myAcList = myJObj.states;
                int myPlanesInZone = 0;
                if (myAcList != null)
                {
                    myPlanesInZone = myAcList.Count;
                }

                _Record.MyLog("RawData", "ProcData", "=== myFuncThread(): Отпарсили строку в JSON. Количество самолетов в зоне по поступившим данным = " + myPlanesInZone);

                // Создадим из объекта JSON структуру, добавим ее в словарный массив (или перепишем, если такая уже имеется)

                for (int i = 0; i < myPlanesInZone; i++)
                {
                    // Создаем одиночные экземпляры структур
                    myPlaneParameters_OpenSky myOnePlanePars = new myPlaneParameters_OpenSky(); // Большая структура
                    MyPlaneVisual myPlane = new MyPlaneVisual(); // Малая структура
                    MyFlightHistory myOnePlaneHist = new MyFlightHistory(); // История самолета
                    String myKey; // Уникальный ключ самолета (код ICAO)

                    // *************************************************************************************************************
                    // Начинаем заполнять экземпляр структуры myOnePlanePars (Большой) из объекта JSON - основные данные
                    _Record.MyLog("ProcData", "=== myFuncThread(): " + i + " Создали структуры, начинаем заполнять большую");

                    // JSON-массив параметров для одного самолета, полученный из JSON (элементы массива разных типов)
                    JArray myAcItem = (JArray)myAcList[i];

                    float myFloat;
                    int myInt;
                    long myLong;
                    bool myBool;

                    //myOnePlanePars.Id = myJObj.acList[i].Id; // The Unique Identifier of the aircraft (in current tracking session?). Virtual Radar Server.

                    // Код ИКАО самолета. Unique ICAO 24-bit address of the transponder in hex string representation.
                    myOnePlanePars.icao24 = myAcItem[0].ToString();
                    // Это также будет ключ для записи параметров данного самолета в словарь
                    myKey = myOnePlanePars.icao24;

                    _Record.MyLog("ProcData", "=== myFuncThread(): " + i + " Установили ключ = " + myKey);

                    // Позывной самолета. Callsign of the vehicle (8 chars). Can be null if no callsign has been received.
                    myOnePlanePars.callsign = myAcItem[1].ToString();

                    //_Record.MyLog("ProcData", "=== myFuncThread(): " + i + " Позывной самолета = " + myOnePlanePars.callsign);

                    // Country name inferred from the ICAO 24-bit address.
                    myOnePlanePars.origin_country = myAcItem[2].ToString();

                    //_Record.MyLog("ProcData", "=== myFuncThread(): " + i + " origin_country = " + myOnePlanePars.origin_country);

                    // Время последних данных о положении самолета
                    // Unix timestamp (seconds) for the last position update. Can be null if no position report was received by OpenSky within the past 15s.
                    if (long.TryParse(myAcItem[3].ToString(), out myLong))
                    {
                        myOnePlanePars.time_position = myLong;
                    }
                    else
                    {
                        myOnePlanePars.time_position = 0;
                    }
                    //_Record.MyLog("ProcData", "=== myFuncThread(): " + i + " Время определения положения самолета = " + myOnePlanePars.time_position);

                    // Время последних данных от самолета вообще
                    // Unix timestamp (seconds) for the last update in general. This field is updated for any new, valid message received from the transponder.
                    if (long.TryParse(myAcItem[4].ToString(), out myLong))
                    {
                        myOnePlanePars.last_contact = myLong;
                    }
                    else
                    {
                        myOnePlanePars.last_contact = 0;
                    }
                    //_Record.MyLog("ProcData", "=== myFuncThread(): " + i + " Время последних данных от самолета = " + myOnePlanePars.last_contact);

                    // Долгота
                    // WGS-84 longitude in decimal degrees. Can be null.
                    if (Single.TryParse(myAcItem[5].ToString(), out myFloat))
                    {
                        myOnePlanePars.longitude = myFloat;
                    }
                    else
                    {
                        myOnePlanePars.longitude = 0;
                    }
                    //_Record.MyLog("ProcData", "=== myFuncThread(): " + i + " Долгота = " + myOnePlanePars.longitude);
                    // Широта
                    // WGS-84 latitude in decimal degrees. Can be null.
                    if (Single.TryParse(myAcItem[6].ToString(), out myFloat))
                    {
                        myOnePlanePars.latitude = myFloat;
                    }
                    else
                    {
                        myOnePlanePars.latitude = 0;
                    }
                    //_Record.MyLog("ProcData", "=== myFuncThread(): " + i + " Широта = " + myOnePlanePars.latitude);
                    // Высота барометрическая
                    // Barometric altitude in meters. Can be null.
                    if (Single.TryParse(myAcItem[7].ToString(), out myFloat))
                    {
                        myOnePlanePars.baro_altitude = myFloat;
                    }
                    else
                    {
                        myOnePlanePars.baro_altitude = 0;
                    }
                    //_Record.MyLog("ProcData", "=== myFuncThread(): " + i + " Высота барометрическая = " + myOnePlanePars.baro_altitude);
                    // Признак "самолет на земле"
                    // Boolean value which indicates if the position was retrieved from a surface position report.
                    if (bool.TryParse(myAcItem[8].ToString(), out myBool))
                    {
                        myOnePlanePars.on_ground = myBool;
                    }
                    else
                    {
                        myOnePlanePars.on_ground = false;
                    }
                    //_Record.MyLog("ProcData", "=== myFuncThread(): " + i + " Самолет на земле = " + myOnePlanePars.on_ground);
                    // Скорость относительно земли в метрах в секунду
                    // Velocity over ground in m/s. Can be null.
                    if (Single.TryParse(myAcItem[9].ToString(), out myFloat))
                    {
                        myOnePlanePars.velocity = myFloat;
                    }
                    else
                    {
                        myOnePlanePars.velocity = 0;
                    }
                    //_Record.MyLog("ProcData", "=== myFuncThread(): " + i + " Скорость = " + myOnePlanePars.velocity);
                    // Истинный курс в градусах
                    // True track in decimal degrees clockwise from north (north=0°). Can be null.
                    if (Single.TryParse(myAcItem[10].ToString(), out myFloat))
                    {
                        myOnePlanePars.true_track = myFloat;
                    }
                    else
                    {
                        myOnePlanePars.true_track = 0;
                    }
                    //_Record.MyLog("ProcData", "=== myFuncThread(): " + i + " Истинный курс = " + myOnePlanePars.true_track);
                    // Вертикальная скорость в м/сек
                    // Vertical rate in m/s. A positive value indicates that the airplane is climbing, a negative value indicates that it descends. Can be null.
                    if (Single.TryParse(myAcItem[11].ToString(), out myFloat))
                    {
                        myOnePlanePars.vertical_rate = myFloat;
                    }
                    else
                    {
                        myOnePlanePars.vertical_rate = 0;
                    }
                    //_Record.MyLog("ProcData", "=== myFuncThread(): " + i + " Вертикальная скорость = " + myOnePlanePars.vertical_rate);

                    // Массив ID ресиверов
                    // IDs of the receivers which contributed to this state vector. Is null if no filtering for sensor was used in the request.

                    // Высота по GPS
                    // Geometric altitude in meters. Can be null.
                    if (Single.TryParse(myAcItem[13].ToString(), out myFloat))
                    {
                        myOnePlanePars.geo_altitude = myFloat;
                    }
                    else
                    {
                        myOnePlanePars.geo_altitude = 0;
                    }
                    //_Record.MyLog("ProcData", "=== myFuncThread(): " + i + " Высота по GPS = " + myOnePlanePars.geo_altitude);
                    // Squawk код
                    // The transponder code aka Squawk. Can be null.
                    myOnePlanePars.squawk = myAcItem[14].ToString();
                    //_Record.MyLog("ProcData", "=== myFuncThread(): " + i + " Squawk код = " + myOnePlanePars.squawk);
                    // Признак особого полета
                    // Whether flight status indicates special purpose indicator.
                    if (bool.TryParse(myAcItem[15].ToString(), out myBool))
                    {
                        myOnePlanePars.spi = myBool;
                    }
                    else
                    {
                        myOnePlanePars.spi = false;
                    }
                    //_Record.MyLog("ProcData", "=== myFuncThread(): " + i + " Особый полет = " + myOnePlanePars.spi);
                    // Origin of this state’s position: 0 = ADS-B, 1 = ASTERIX, 2 = MLAT
                    if (int.TryParse(myAcItem[16].ToString(), out myInt))
                    {
                        myOnePlanePars.position_source = myInt;
                    }
                    else
                    {
                        myOnePlanePars.position_source = 0;
                    }
                    //_Record.MyLog("ProcData", "=== myFuncThread(): " + i + " Источник = " + myOnePlanePars.position_source);

                    _Record.MyLog("ProcData", "=== myFuncThread(): " + i + " ключ: " + myKey + " Код ИКАО: " + myOnePlanePars.icao24 + " Позывной: " + myOnePlanePars.callsign + " Страна: " + myOnePlanePars.origin_country + " Pos Time: " + myOnePlanePars.time_position + " Last Contact: " + myOnePlanePars.last_contact + " Долгота: " + myOnePlanePars.longitude + " Широта: " + myOnePlanePars.latitude + " Высота бар.: " + myOnePlanePars.baro_altitude + " На земле: " + myOnePlanePars.on_ground + " Скорость: " + myOnePlanePars.velocity + "Курс: " + myOnePlanePars.true_track + " Верт. скорость: " + myOnePlanePars.vertical_rate + " Высота GPS: " + myOnePlanePars.geo_altitude + " Squawk: " + myOnePlanePars.squawk + " SPI: " + myOnePlanePars.spi + " Источник: " + myOnePlanePars.position_source);

                    _Record.MyLog("ProcData", "=== myFuncThread(): " + i + " ключ = " + myKey + " Заполнили экземпляр большой структуры. Проверим данные самолета на новизну.");

                    // Проверим: может быть данные, полученные для этого самолета, совпадают с предыдущими, уже записанными в словаре myAllPlanesPars
                    myPlaneParameters_OpenSky myOnePlanePreviosPars;
                    bool myPlaneIsNew = true;
                    bool myPlaneBackData = false;
                    bool myPlaneFrozen = false;
                    bool myPlaneBadPos = false;
                    _Record.MyLog("ProcData", "=== myFuncThread(): " + i + " ключ = " + myKey + " Попробуем найти с таким же ключом");
                    if (myAllPlanesPars.TryGetValue(myKey, out myOnePlanePreviosPars)) // Если есть самолет с таким ключом считаем его параметры в экземпляр большой структуры myOnePlanePreviosPars
                    {
                        _Record.MyLog("ProcData", "=== myFuncThread(): " + i + " ключ = " + myKey + " Да, самолет с таким ключом уже есть");
                        myPlaneIsNew = false;

                        if (myOnePlanePars.time_position <= myOnePlanePreviosPars.time_position) // Новое время данных о положении самолета меньше или равно номеру предыдущему.
                        {
                            myPlaneBackData = true;
                            _Record.MyLog("ProcData", "=== myFuncThread(): " + i + " ключ = " + myKey + " Новое время данных о положении самолета меньше или равен номеру предыдущего. Новое time_position = " + myOnePlanePars.time_position + ", старое = " + myOnePlanePreviosPars.time_position);
                        }
                        else if (myOnePlanePars.latitude == myOnePlanePreviosPars.latitude & myOnePlanePars.longitude == myOnePlanePreviosPars.longitude) // Новые и старые координаты совпадают
                        {
                            myPlaneFrozen = true;
                            _Record.MyLog("ProcData", "=== myFuncThread(): " + i + " ключ = " + myKey + " Новые и старые координаты совпадают");
                        }
                        else
                        {
                            _Record.MyLog("ProcData", "=== myFuncThread(): " + i + " ключ = " + myKey + " Новые и старые координаты не совпадают");
                        }
                    }
                    else // Возможно, появился новый самолет.
                    {
                        _Record.MyLog("ProcData", "=== myFuncThread(): " + i + " ключ = " + myKey + " Нет, самолета с таким ключом нет");
                        // Проверим, может это уже удаленный, который недавно сел
                        if (myLandedPlanes.ContainsKey(myKey)) // Самолет есть в списке севших
                        {
                            _Record.MyLog("ProcData", "=== myFuncThread(): " + i + " ключ = " + myKey + " Но самолет с таким ключом есть в списке севших");
                            if (myStartProcTime - 600000 < myLandedPlanes[myKey]) // Самолет сел менее 10 минут назад
                            {
                                _Record.MyLog("ProcData", "=== myFuncThread(): " + i + " ключ = " + myKey + " Самолет сел менее 10 минут назад. Ничего делать не будем, переходим к следующему самолету");
                                continue; // Ничего делать не будем, переходим к следующему самолету
                            }
                            _Record.MyLog("ProcData", "=== myFuncThread(): " + i + " ключ = " + myKey + " Самолет сел более 10 минут назад. Удаляем из списка севших, продолжаем обработку его данных");
                            myLandedPlanes.Remove(myKey); // Удаляем самолет из списка севших, продолжаем обработку его данных
                        }
                        try // Создадим лог-файлы для самолета
                        {
                            _Record.AddToDic(myKey);
                            _Record.AddToDic(myKey + "_Data");
                        }
                        catch (Exception myEx)
                        {
                            _Record.MyLog("ProcData", "=== myFuncThread(): Создание лог-файла для самолета с ключом = " + myKey + " Ошибка: " + myEx.Message);
                        }
                    }

                    _Record.MyLog("ProcData", "=== myFuncThread(): " + i + " ключ = " + myKey + " New = " + myPlaneIsNew + " BackData = " + myPlaneBackData + " Frozen = " + myPlaneFrozen + " Далее продолжим заполнять большую структуру из объекта JSON");

                    // Продолжим заполнять большую структуру из объекта JSON
                    //myOnePlanePars = MyFuncBigStructure(myOnePlanePars, myJObj, i, myKey);

                    _Record.MyLog("ProcData", "=== myFuncThread(): " + i + " ключ = " + myKey + " Разбор полученных данных JSON и заполнение основной структуры myOnePlanePars закончены. Начнем заполнять малую");

                    // Разбор полученных данных JSON и заполнение основной структуры myOnePlanePars для текущего самолета закончены.
                    // *************************************************************************************************************
                    // Преобразуем данные и заполним экземпляр рабочей структуры myPlane (Малая)
                    // Поля GO, Banner1 и Banner1TextX заполняются позже, при создании или обновлении записей в словарях

                    myPlane.Key = myKey;
                    myPlane.Call = myOnePlanePars.callsign; // Позывной
                    myPlane.Icao = myOnePlanePars.icao24; // HEX код ICAO (ADS-B Mode-S код)
                    myPlane.Time = myStartProcTime; // Время последней порции данных
                    myPlane.TargetTime = myStartProcTime + myLag; // Целевое время для полета к последней известной точке.
                    myPlane.NeedNewBezier = true; // Требуется определить новые путевые точки для полета по Безье. Выполняется в Update().

                    // Высота в футах. На самом деле разборка должна быть более сложная, с учетом GAlt и AltT, а также, возможно, InHG
                    _Record.MyLog("ProcData", "=== myFuncThread(): " + i + " ключ = " + myKey + " myOnePlanePars.on_ground = " + myOnePlanePars.on_ground + " myOnePlanePars.baro_altitude = " + myOnePlanePars.baro_altitude + " myOnePlanePars.geo_altitude = " + myOnePlanePars.geo_altitude);
                    if (myOnePlanePars.on_ground || myOnePlanePars.geo_altitude == 0)
                    {
                        myPlane.Alt = "Gnd";
                    }
                    else
                    {
                        myPlane.Alt = (myOnePlanePars.geo_altitude / myFeet).ToString();
                    }
                    _Record.MyLog("ProcData", "=== myFuncThread(): " + i + " ключ = " + myKey + " Высота = " + myPlane.Alt);


                    // Скорость

                    //myPlane.Speed.y = myOnePlanePars.Vsi * myFeet / 60.0f;
                    //myPlane.Speed.x = myOnePlanePars.Spd * myKnot * Mathf.Sin(myOnePlanePars.Trak * Mathf.Deg2Rad);
                    //myPlane.Speed.z = myOnePlanePars.Spd * myKnot * Mathf.Cos(myOnePlanePars.Trak * Mathf.Deg2Rad);
                    myPlane.Speed.y = myOnePlanePars.vertical_rate;
                    myPlane.Speed.x = myOnePlanePars.velocity * Mathf.Sin(myOnePlanePars.true_track * Mathf.Deg2Rad);
                    myPlane.Speed.z = myOnePlanePars.velocity * Mathf.Cos(myOnePlanePars.true_track * Mathf.Deg2Rad);


                    // Положение

                    if (myPlanesHistory.TryGetValue(myKey, out myOnePlaneHist)) // история данного самолета
                    {
                        // число записей в истории
                        int k = myOnePlaneHist.Time.Count;
                        // Готовим прогноз на новое положение
                        Vector3 myLastPosition = myOnePlaneHist.Position[k - 1];
                        Vector3 myPredictedPosition = myLastPosition + myOnePlaneHist.Speed[k - 1] * myLastDeltaTime / 1000;
                        _Record.MyLog(myKey, "ProcData", "=== myFuncThread(): ключ = " + myKey + " Прогноз. LastDeltaTime = " + myLastDeltaTime + " Previous Position =" + myOnePlaneHist.Position[k - 1] + " Previous Speed = " + myOnePlaneHist.Speed[k - 1] + " Predicted Position = " + myPredictedPosition);

                        if (myPlaneBackData) // Если получен пакет данных с устаревшим временем, то "летим по прогнозу"
                        {
                            myPlane.Position = myPredictedPosition;
                            myPlane.BadPosCounter = myOnePlaneHist.BadPosCounter[k - 1];
                            _Record.MyLog(myKey, "ProcData", "=== myFuncThread(): ключ = " + myKey + " Данные полета - устаревшее время, летим по прогнозу. Новое время = " + myOnePlanePars.time_position + ", старое = " + myOnePlanePreviosPars.time_position + ", myPlane.Position = " + myPlane.Position);
                        }
                        else if (myPlaneFrozen) // Если данные полета заморожены, то "летим по прогнозу"
                        {
                            myPlane.Position = myPredictedPosition;
                            myPlane.BadPosCounter = myOnePlaneHist.BadPosCounter[k - 1];
                            _Record.MyLog(myKey, "ProcData", "=== myFuncThread(): ключ = " + myKey + " Данные полета не изменились, летим по прогнозу. myPlane.Position = " + myPlane.Position);
                        }
                        else
                        {
                            // Преобразуем полученные данные (высоту в футах и географические координаты) в положение в пространстве сцены
                            Vector3 myResponsedPosition;
                            myResponsedPosition.y = Mathf.Max(myOnePlanePars.geo_altitude - myAirport_ALt, 0.0f); // Высота
                            Vector2d worldPosition = Conversions.GeoToWorldPosition(myOnePlanePars.latitude, myOnePlanePars.longitude, myCenterMercator, myWorldRelativeScale);
                            myResponsedPosition.x = (float)worldPosition.x;
                            myResponsedPosition.z = (float)worldPosition.y;
                            myResponsedPosition = myResponsedPosition + myPosShift;
                            myPlane.RawPosition = myResponsedPosition; // сразу запомним полученное положение (для отладки - ручного анализа)
                            _Record.MyLog(myKey, "ProcData", "=== myFuncThread(): ключ = " + myKey + " Полученные данные. Raw Position = " + myPlane.RawPosition);

                            // Отставание данных о положении самолета (разница между исходным временем позиционирования (time_position) и временем на которое идет расчет положения (myStartProcTime))
                            myPosDelay = (int)(myStartProcTime - (myOnePlanePars.time_position - _Time.UnixStartTime) * 1000);
                            _Record.MyLog(myKey, "ProcData", "=== myFuncThread(): О задержке полученных данных: myStartProcTime = " + myStartProcTime + " time_position = " + myOnePlanePars.time_position + " myPosDelay = " + myPosDelay);
                            myResponsedPosition = myResponsedPosition + myPlane.Speed * myPosDelay / 1000f;
                            _Record.MyLog(myKey, "ProcData", "=== myFuncThread(): ключ = " + myKey + " Полученные данные. Responsed Position = " + myResponsedPosition);

                            // Сравним прогноз и полученные данные
                            Vector3 myPredictedVector = myPredictedPosition - myLastPosition;
                            Vector3 myResponsedVector = myResponsedPosition - myLastPosition;
                            Vector3 myDeltaVector = myResponsedPosition - myPredictedPosition;
                            Vector2 myPredictedHorVector = new Vector2 ( myPredictedVector.x, myPredictedVector.z );
                            Vector2 myResponsedHorVector = new Vector2(myResponsedVector.x, myResponsedVector.z);
                            float myAngle = Vector3.Angle(myPredictedVector, myResponsedVector);
                            _Record.MyLog(myKey, "ProcData", "=== myFuncThread(): Будем искать плохие данные. Время: myStartProcTime = " + myStartProcTime);
                            _Record.MyLog(myKey, "ProcData", "=== myFuncThread(): Предсказано: myPredictedPosition = " + myPredictedPosition + " Получено: Raw Position = " + myPlane.RawPosition + " с поправкой на время: myResponsedPosition = " + myResponsedPosition);
                            _Record.MyLog(myKey, "ProcData", "=== myFuncThread(): myPredictedVector = " + myPredictedVector + " myResponsedVector = " + myResponsedVector + " myDeltaVector " + myDeltaVector);
                            _Record.MyLog(myKey, "ProcData", "=== myFuncThread(): Угол между направлением на прогноз и полученной точкой (град.) = " + myAngle);
                            if(k < 3)
                            {
                                myPlane.Position = myResponsedPosition;
                                myPlane.BadPosCounter = 0;
                                _Record.MyLog(myKey, "ProcData", "=== myFuncThread(): ключ = " + myKey + " В истории не больше 2х точек. Данные полета считаем достоверными, летим по ним. myPlane.Position = " + myPlane.Position);
                            }
                            //else if (myAngle > 120.0f)
                            //{
                            //    myPlaneBadPos = true;
                            //    myPlane.Position = myPredictedPosition;
                            //    _Record.MyLog(myKey, "ProcData", "=== myFuncThread(): ключ = " + myKey + " Данные полета считаем недостоверными. Угол между направлением на прогноз и полученной точкой (град.) = " + myAngle + ". Летим по прогнозу. myPlane.Position = " + myPlane.Position);
                            //}
                            else if ((myOnePlaneHist.PredictionReason[k-1] == "BackData") && (myOnePlaneHist.PredictionReason[k - 2] == "BackData") && (myOnePlaneHist.PredictionReason[k - 3] == "BackData") || (myOnePlaneHist.PredictionReason[k - 1] == "Frozen") && (myOnePlaneHist.PredictionReason[k - 2] == "Frozen") && (myOnePlaneHist.PredictionReason[k - 3] == "Frozen"))
                            {
                                myPlane.Position = myResponsedPosition;
                                myPlane.BadPosCounter = 0;
                                _Record.MyLog(myKey, "ProcData", "=== myFuncThread(): ключ = " + myKey + " Три предыдущие точки были BackData или Frozen. Данные полета считаем достоверными, летим по ним. myPlane.Position = " + myPlane.Position);
                            }
                            else if (myOnePlaneHist.BadPosCounter[k - 1] >= 3)
                            {
                                myPlane.Position = myResponsedPosition;
                                myPlane.BadPosCounter = 0;
                                _Record.MyLog(myKey, "ProcData", "=== myFuncThread(): ключ = " + myKey + " BadPos уже был раз подряд: " + myOnePlaneHist.BadPosCounter[k - 1] + ". Данные полета считаем достоверными, летим по ним. myPlane.Position = " + myPlane.Position);
                            }
                            else if (myDeltaVector.sqrMagnitude < 22500)
                            {
                                myPlane.Position = myResponsedPosition;
                                myPlane.BadPosCounter = 0;
                                _Record.MyLog(myKey, "ProcData", "=== myFuncThread(): ключ = " + myKey + " Отклонение от прогноза менее 150 метров. myDeltaVector.sqrMagnitude = " + myDeltaVector.sqrMagnitude + ". Данные полета считаем достоверными, летим по ним. myPlane.Position = " + myPlane.Position);
                            }
                            else if ((Mathf.Abs(myDeltaVector.y) > 200) && (myPredictedVector.y == 0.0f || (myPredictedVector.y != 0.0f && (Mathf.Abs(myResponsedVector.y / myPredictedVector.y)) > 5)))
                            {
                                myPlaneBadPos = true;
                                myPlane.Position = myPredictedPosition;
                                myPlane.BadPosCounter = myOnePlaneHist.BadPosCounter[k - 1] + 1;
                                _Record.MyLog(myKey, "ProcData", "=== myFuncThread(): ключ = " + myKey + " Данные полета считаем недостоверными - " + myPlane.BadPosCounter + ". Скачок по вертикали (метров) = " + myDeltaVector.y + ", коэф. скорости = " + (myResponsedVector.y / myPredictedVector.y) + ". Летим по прогнозу. myPlane.Position = " + myPlane.Position);
                            }
                            else if (myPredictedHorVector.sqrMagnitude != 0 && (myResponsedHorVector.sqrMagnitude / myPredictedHorVector.sqrMagnitude) > 25.0f)
                            {
                                myPlaneBadPos = true;
                                myPlane.Position = myPredictedPosition;
                                myPlane.BadPosCounter = myOnePlaneHist.BadPosCounter[k - 1] + 1;
                                _Record.MyLog(myKey, "ProcData", "=== myFuncThread(): ключ = " + myKey + " Данные полета считаем недостоверными (скачок) - " + myPlane.BadPosCounter + ", летим по прогнозу. myPlane.Position = " + myPlane.Position);
                            }
                            else
                            {
                                myPlane.Position = myResponsedPosition;
                                myPlane.BadPosCounter = 0;
                                _Record.MyLog(myKey, "ProcData", "=== myFuncThread(): ключ = " + myKey + " Данные полета считаем достоверными, летим по ним. myPlane.Position = " + myPlane.Position);
                            }
                        }
                    }
                    else // истории данного самолета еще нет
                    {
                        Vector3 myResponsedPosition;
                        myResponsedPosition.y = Mathf.Max(myOnePlanePars.geo_altitude - myAirport_ALt, 0.0f); // Высота
                        Vector2d worldPosition = Conversions.GeoToWorldPosition(myOnePlanePars.latitude, myOnePlanePars.longitude, myCenterMercator, myWorldRelativeScale);

                        _Record.MyLog(myKey, "ProcData", "=== myFuncThread(): ключ = " + myKey + " !!!! Lat = " + myOnePlanePars.latitude + " Long = " + myOnePlanePars.longitude + " myCenterMercator = " + myCenterMercator + " myWorldRelativeScale = " + myWorldRelativeScale + " worldPosition = " + worldPosition);

                        myResponsedPosition.x = (float)worldPosition.x;
                        myResponsedPosition.z = (float)worldPosition.y;
                        myPlane.Position = myResponsedPosition + myPosShift;
                        myPlane.BadPosCounter = 0;
                        _Record.MyLog(myKey, "ProcData", "=== myFuncThread(): ключ = " + myKey + " Истории еще нет. Данные полета считаем достоверными, летим по ним. myPlane.Position = " + myPlane.Position);
                    }

                    // Углы

                    // Курсовой угол (угол рыскания)
                    myPlane.Euler.y = myOnePlanePars.true_track;

                    // Угол тангажа
                    float myHorSpeed = myOnePlanePars.velocity; // горизонтальная скорость м/сек
                    float myPitch = 0f;  // угол тангажа
                    if (myHorSpeed != 0f && myPlane.Position.y > 0f) // если горизонтальная скорость не равна 0, а высота больше 0
                    {
                        myPitch = - Mathf.Atan2(myOnePlanePars.vertical_rate, myHorSpeed) * Mathf.Rad2Deg;
                    }
                    myPlane.Euler.x = myPitch;

                    //// Скорость
                    ////myPlane.Speed.y = myOnePlanePars.Vsi * myFeet / 60.0f;
                    ////myPlane.Speed.x = myOnePlanePars.Spd * myKnot * Mathf.Sin(myOnePlanePars.Trak * Mathf.Deg2Rad);
                    ////myPlane.Speed.z = myOnePlanePars.Spd * myKnot * Mathf.Cos(myOnePlanePars.Trak * Mathf.Deg2Rad);
                    //myPlane.Speed.y = myOnePlanePars.vertical_rate;
                    //myPlane.Speed.x = myOnePlanePars.velocity * Mathf.Sin(myOnePlanePars.true_track * Mathf.Deg2Rad);
                    //myPlane.Speed.z = myOnePlanePars.velocity * Mathf.Cos(myOnePlanePars.true_track * Mathf.Deg2Rad);

                    _Record.MyLog(myKey, "ProcData", "=== myFuncThread(): ключ = " + myKey + " BackData = " + myPlaneBackData + " myPlaneFrozen = " + myPlaneFrozen + " myPlaneBadPos = " + myPlaneBadPos);

                    if (myPlaneBackData)
                    {
                        myPlane.PredictionReason = "BackData";
                    }
                    else if (myPlaneFrozen)
                    {
                        myPlane.PredictionReason = "Frozen";
                    }
                    else if (myPlaneBadPos)
                    {
                        myPlane.PredictionReason = "BadPos";
                    }
                    else
                    {
                        myPlane.PredictionReason = "Web";
                    }

                    _Record.MyLog(myKey, "ProcData", "=== myFuncThread(): ключ = " + myKey + " Заполнили экземпляр малой структуры.");

                    // Преобразование и разбор данных завершены. Запишем измененные данные в словари и пополним историю

                    if (myPlaneIsNew)
                    {
                        myAllPlanesPars.Add(myKey, myOnePlanePars); // Добавим большую структуру в словарь myAllPlanesPars
                        myPlaneVis.Add(myKey, myPlane); // Добавим малую структуру в словарь myPlaneVis
                        // Заведем для нового самолета историю
                        myOnePlaneHist.Time = new List<long> { myStartProcTime };
                        myOnePlaneHist.PosTime = new List<long> { myOnePlanePars.time_position };
                        myOnePlaneHist.PredictionReason = new List<String> { myPlane.PredictionReason };
                        myOnePlaneHist.RawPosition = new List<Vector3> { myPlane.RawPosition };
                        myOnePlaneHist.BadPosCounter = new List<int> { myPlane.BadPosCounter };
                        myOnePlaneHist.Position = new List<Vector3> { myPlane.Position };
                        myOnePlaneHist.Euler = new List<Vector3> { myPlane.Euler };
                        myOnePlaneHist.Speed = new List<Vector3> { myPlane.Speed };
                        myPlanesHistory.Add(myKey, myOnePlaneHist);
                        _Record.MyLog(myKey, "ProcData", "=== myFuncThread(): ключ = " + myKey + " Создали самолет, завели новую историю и добавили по записи во все словари.");
                    }
                    else
                    {
                        // Обновим большую структуру в словаре myAllPlanesPars
                        myAllPlanesPars[myKey] = myOnePlanePars;

                        // Обновим малую структуру в словаре myPlaneVis

                        // Сначала возьмем сохраненные ранее параметры
                        myPlane.GO = myPlaneVis[myKey].GO; // Указатель на Game Object
                        myPlane.Banner1 = myPlaneVis[myKey].Banner1;
                        myPlane.Banner1Call = myPlaneVis[myKey].Banner1Call;
                        myPlane.Banner1Icao = myPlaneVis[myKey].Banner1Icao;
                        myPlane.Banner1PReason = myPlaneVis[myKey].Banner1PReason;
                        myPlane.Banner1Model = myPlaneVis[myKey].Banner1Model;
                        myPlane.Banner1Alt = myPlaneVis[myKey].Banner1Alt;
                        myPlane.Banner1Panel = myPlaneVis[myKey].Banner1Panel;
                        myPlane.Model = myPlaneVis[myKey].Model;
                        myPlane.TrackPoints = myPlaneVis[myKey].TrackPoints;
                        myPlane.StartBezier = myPlaneVis[myKey].StartBezier;
                        myPlane.TrackTime = myPlaneVis[myKey].TrackTime;
                        myPlane.PrevTargetTime = myPlaneVis[myKey].PrevTargetTime;

                        myPlaneVis[myKey] = myPlane; // А вот теперь обновим малую структуру в словаре myPlaneVis

                        // Пополним историю самолета
                        // Извлечем из словаря структуру с историей
                        myOnePlaneHist = myPlanesHistory[myKey];
                        // Пополним каждый исторический массив
                        myOnePlaneHist.Time.Add(myStartProcTime);
                        myOnePlaneHist.PosTime.Add(myOnePlanePars.time_position);
                        myOnePlaneHist.PredictionReason.Add(myPlane.PredictionReason);
                        myOnePlaneHist.RawPosition.Add(myPlane.RawPosition);
                        myOnePlaneHist.BadPosCounter.Add(myPlane.BadPosCounter);
                        myOnePlaneHist.Position.Add(myPlane.Position);
                        myOnePlaneHist.Euler.Add(myPlane.Euler);
                        myOnePlaneHist.Speed.Add(myPlane.Speed);
                        // Держим в истории конечное количество точек
                        if (myOnePlaneHist.Time.Count > 50)
                        {
                            myOnePlaneHist.Time.RemoveAt(0);
                            myOnePlaneHist.PosTime.RemoveAt(0);
                            myOnePlaneHist.PredictionReason.RemoveAt(0);
                            myOnePlaneHist.RawPosition.RemoveAt(0);
                            myOnePlaneHist.BadPosCounter.RemoveAt(0);
                            myOnePlaneHist.Position.RemoveAt(0);
                            myOnePlaneHist.Euler.RemoveAt(0);
                            myOnePlaneHist.Speed.RemoveAt(0);
                        }
                        // Обновим историю в словаре
                        myPlanesHistory[myKey] = myOnePlaneHist;
                        _Record.MyLog(myKey, "ProcData", "=== myFuncThread(): ключ = " + myKey + " Обновили по записи во всех словарях. Точек в истории: " + myOnePlaneHist.Time.Count);
                        // Распечатаем историю
                        _Record.MyLog(myKey, "Time\tPosTime\tPredict\tBad\tPosX\tPosY\tPosZ\tRawX\tRawY\tRawZ\tPitch\tYaw\tRoll\tSpeedX\tSpeedY\tSpeedZ", false);
                        for (int j = 0; j < myOnePlaneHist.Time.Count; j++)
                        {
                            String myLine = myOnePlaneHist.Time[j] + "\t" + myOnePlaneHist.PosTime[j] + "\t" + myOnePlaneHist.PredictionReason[j] + "\t" + myOnePlaneHist.BadPosCounter[j] + "\t" +
                                myOnePlaneHist.Position[j].x.ToString(CultureInfo.GetCultureInfo("ru-RU")) + "\t" + myOnePlaneHist.Position[j].y.ToString(CultureInfo.GetCultureInfo("ru-RU")) + "\t" + myOnePlaneHist.Position[j].z.ToString(CultureInfo.GetCultureInfo("ru-RU")) + "\t" +
                                myOnePlaneHist.RawPosition[j].x.ToString(CultureInfo.GetCultureInfo("ru-RU")) + "\t" + myOnePlaneHist.RawPosition[j].y.ToString(CultureInfo.GetCultureInfo("ru-RU")) + "\t" + myOnePlaneHist.RawPosition[j].z.ToString(CultureInfo.GetCultureInfo("ru-RU")) + "\t" +
                                myOnePlaneHist.Euler[j].x.ToString(CultureInfo.GetCultureInfo("ru-RU")) + "\t" + myOnePlaneHist.Euler[j].y.ToString(CultureInfo.GetCultureInfo("ru-RU")) + "\t" + myOnePlaneHist.Euler[j].z.ToString(CultureInfo.GetCultureInfo("ru-RU")) + "\t" +
                                myOnePlaneHist.Speed[j].x.ToString(CultureInfo.GetCultureInfo("ru-RU")) + "\t" + myOnePlaneHist.Speed[j].y.ToString(CultureInfo.GetCultureInfo("ru-RU")) + "\t" + myOnePlaneHist.Speed[j].z.ToString(CultureInfo.GetCultureInfo("ru-RU"));
                            _Record.MyLog(myKey, myLine, false);
                        }
                    }
                }
            }
            else // Если новых полетных данных от сервера еще не получили, то строим прогнозы для каждого самолета и летим по ним
            {
                long myCurTime = _Time.CurrentTime();
                myLastDeltaTime = myCurTime - myStartProcTime; // Время последнего выполненного полного цикла обработки данных
                // Строим новые прогнозы и летим по ним, только если новых данных от сервера нет больше 5,5 секунд
                if(myLastDeltaTime > 5500)
                {
                    myStartProcTime = myCurTime; // Время начала нового цикла
                    myStartProcTime2 = myStartProcTime;
                    _Record.MyLog("RawData", "ProcData", "=== myFuncThread(): Новые данные от _WebData.GetWebData() еще не поступили myLastDeltaTime = " + myLastDeltaTime + " myStartProcTime = " + myStartProcTime);

                    List<String> myKeys = new List<String>(myPlaneVisKeys);
                    foreach (String myKey in myKeys)
                    {
                        // Большая и Малая структуры данных и история данного самолета
                        myPlaneParameters_OpenSky myOnePlanePars = myAllPlanesPars[myKey];
                        MyPlaneVisual myPlane = myPlaneVis[myKey];
                        MyFlightHistory myOnePlaneHist = myPlanesHistory[myKey];
                        _Record.MyLog("ProcData", "=== myFuncThread(): Большая и Малая структуры данных и история данного самолета");
                        // Готовим прогноз
                        int k = myOnePlaneHist.Time.Count; // число записей в истории
                        _Record.MyLog("ProcData", "=== myFuncThread(): k = myOnePlaneHist.Time.Count, k=" + k);
                        Vector3 myLastPosition = myOnePlaneHist.Position[k - 1];
                        Vector3 myPredictedPosition = myLastPosition + myOnePlaneHist.Speed[k - 1] * myLastDeltaTime / 1000;
                        _Record.MyLog(myKey, "=== myFuncThread(): Новые данные от _WebData.GetWebData() еще не поступили");
                        _Record.MyLog(myKey, "ProcData", "=== myFuncThread(): ключ = " + myKey + " Прогноз. LastDeltaTime = " + myLastDeltaTime + " Previous Position =" + myOnePlaneHist.Position[k - 1] + " Previous Speed = " + myOnePlaneHist.Speed[k - 1] + " Predicted Position = " + myPredictedPosition);
                        // и "летим" по нему
                        myPlane.Position = myPredictedPosition;
                        myPlane.BadPosCounter = myOnePlaneHist.BadPosCounter[k - 1];
                        _Record.MyLog(myKey, "=== myFuncThread(): ключ = " + myKey + " Летим по прогнозу. myPlane.Position = " + myPlane.Position);

                        string myPrevReason = myOnePlaneHist.PredictionReason[k - 1];
                        if (myPrevReason.StartsWith("NoData"))
                        {
                            myPlane.PredictionReason = myPrevReason + ".";
                        }
                        else
                        {
                            myPlane.PredictionReason = "NoData";
                        }

                        // Требуется определить новые путевые точки для полета по Безье. Выполняется в Update().
                        myPlane.NeedNewBezier = true;
                        myPlane.TargetTime = myStartProcTime + myLag; // Целевое время для полета к последней известной точке.


                        // Обновим малую структуру в словаре myPlaneVis

                        // Сначала возьмем сохраненные ранее параметры
                        myPlane.GO = myPlaneVis[myKey].GO; // Указатель на Game Object
                        myPlane.Banner1 = myPlaneVis[myKey].Banner1;
                        myPlane.Banner1Call = myPlaneVis[myKey].Banner1Call;
                        myPlane.Banner1Icao = myPlaneVis[myKey].Banner1Icao;
                        myPlane.Banner1PReason = myPlaneVis[myKey].Banner1PReason;
                        myPlane.Banner1Model = myPlaneVis[myKey].Banner1Model;
                        myPlane.Banner1Alt = myPlaneVis[myKey].Banner1Alt;
                        myPlane.Banner1Panel = myPlaneVis[myKey].Banner1Panel;
                        myPlane.Model = myPlaneVis[myKey].Model;
                        myPlane.TrackPoints = myPlaneVis[myKey].TrackPoints;
                        myPlane.StartBezier = myPlaneVis[myKey].StartBezier;
                        myPlane.TrackTime = myPlaneVis[myKey].TrackTime;
                        myPlane.PrevTargetTime = myPlaneVis[myKey].PrevTargetTime;

                        myPlaneVis[myKey] = myPlane; // А вот теперь обновим малую структуру в словаре myPlaneVis

                        // Пополним историю самолета
                        // Пополним каждый исторический массив
                        myOnePlaneHist.Time.Add(myStartProcTime);
                        myOnePlaneHist.PosTime.Add(myOnePlanePars.time_position);
                        myOnePlaneHist.PredictionReason.Add(myPlane.PredictionReason);
                        myOnePlaneHist.Position.Add(myPlane.Position);
                        myOnePlaneHist.RawPosition.Add(myPlane.RawPosition);
                        myOnePlaneHist.BadPosCounter.Add(myPlane.BadPosCounter);
                        myOnePlaneHist.Euler.Add(myPlane.Euler);
                        myOnePlaneHist.Speed.Add(myPlane.Speed);
                        // Держим в истории конечное количество точек
                        if (myOnePlaneHist.Time.Count > 50)
                        {
                            myOnePlaneHist.Time.RemoveAt(0);
                            myOnePlaneHist.PosTime.RemoveAt(0);
                            myOnePlaneHist.PredictionReason.RemoveAt(0);
                            myOnePlaneHist.Position.RemoveAt(0);
                            myOnePlaneHist.RawPosition.RemoveAt(0);
                            myOnePlaneHist.BadPosCounter.RemoveAt(0);
                            myOnePlaneHist.Euler.RemoveAt(0);
                            myOnePlaneHist.Speed.RemoveAt(0);
                        }
                        // Обновим историю в словаре
                        myPlanesHistory[myKey] = myOnePlaneHist;
                        _Record.MyLog(myKey, "=== myFuncThread(): ключ = " + myKey + " Обновили по записи в малом и историческом словарях. Точек в истории: " + myOnePlaneHist.Time.Count);
                        // Распечатаем историю
                        _Record.MyLog(myKey, "Time\tPosTime\tPredict\tBad\tPosX\tPosY\tPosZ\tRawX\tRawY\tRawZ\tPitch\tYaw\tRoll\tSpeedX\tSpeedY\tSpeedZ", false);
                        for (int j = 0; j < myOnePlaneHist.Time.Count; j++)
                        {
                            String myLine = myOnePlaneHist.Time[j] + "\t" + myOnePlaneHist.PosTime[j] + "\t" + myOnePlaneHist.PredictionReason[j] + "\t" + myOnePlaneHist.BadPosCounter[j] + "\t" +
                                myOnePlaneHist.Position[j].x.ToString(CultureInfo.GetCultureInfo("ru-RU")) + "\t" + myOnePlaneHist.Position[j].y.ToString(CultureInfo.GetCultureInfo("ru-RU")) + "\t" + myOnePlaneHist.Position[j].z.ToString(CultureInfo.GetCultureInfo("ru-RU")) + "\t" +
                                myOnePlaneHist.RawPosition[j].x.ToString(CultureInfo.GetCultureInfo("ru-RU")) + "\t" + myOnePlaneHist.RawPosition[j].y.ToString(CultureInfo.GetCultureInfo("ru-RU")) + "\t" + myOnePlaneHist.RawPosition[j].z.ToString(CultureInfo.GetCultureInfo("ru-RU")) + "\t" +
                                myOnePlaneHist.Euler[j].x.ToString(CultureInfo.GetCultureInfo("ru-RU")) + "\t" + myOnePlaneHist.Euler[j].y.ToString(CultureInfo.GetCultureInfo("ru-RU")) + "\t" + myOnePlaneHist.Euler[j].z.ToString(CultureInfo.GetCultureInfo("ru-RU")) + "\t" +
                                myOnePlaneHist.Speed[j].x.ToString(CultureInfo.GetCultureInfo("ru-RU")) + "\t" + myOnePlaneHist.Speed[j].y.ToString(CultureInfo.GetCultureInfo("ru-RU")) + "\t" + myOnePlaneHist.Speed[j].z.ToString(CultureInfo.GetCultureInfo("ru-RU"));
                            _Record.MyLog(myKey, myLine, false);
                        }
                    }
                }
                else // 
                {
                    myStartProcTime2 = myCurTime;
                    _Record.MyLog("RawData", "ProcData", "=== myFuncThread(): Новых данных от сервера нет, но прогноз пока не строим.");
                }
            }

            _Record.MyLog("RawData", "ProcData", "=== myFuncThread(): Первичная обработка данных завершена.");
            myPrimaryDataProc = false;
            mySecondaryDataProc = true;
        }
    }


    // Вторичная обработка полетных данные в корутине
    IEnumerator myFuncProcData()
    {

        while (true)
        {
            //_Record.MyLog("**********************************************************************");

            // Ждем заверешения первичной обработки данных  в потоке
            _Record.MyLog("RawData", "ProcData", "%%% myFuncProcData(): Начинаем ждать выполнения первичной обработки данных");
            _Record.MyLog("RawData", "ProcData", "**********************************************************************");
            long myStartWaitTime = _Time.CurrentTime();
            while (myPrimaryDataProc)
            {
                yield return new WaitForSeconds(0.01f);
            }

            long mySecondProcStartTime = _Time.CurrentTime();

            _Record.MyLog("RawData", "ProcData", "%%% myFuncProcData(): Обнаружено, что первичная обработка данных завершена. myPrimaryDataProc = " + myPrimaryDataProc + ". Текущее время = " + _Time.CurrentTime() + ". Время ожидания первичной обработки = " + (_Time.CurrentTime() - myStartWaitTime));
            _Record.MyLog("RawData", "ProcData", "%%% myFuncProcData(): Начало вторичной обработки данных. Выполняется в основном потоке в корутине myFuncProcData()");

            // Обработаем все самолеты по словарю myPlaneVis - создадим новые, удалим устаревшие

            // Список ключей малого словаря (сформирован из коллекции ключей)
            List<String> myKeys = new List<String>(myPlaneVis.Keys);
            for (int i = 0; i < myKeys.Count; i++)
            {
                _Record.MyLog("ProcData", "%%% myFuncProcData(): Самолет № " + i + ". Ключ = " + myKeys[i]);
                // Признак того, что самолет подлежит удалению
                bool myPlaneToDelete = false;
                // Признак того, что самолет приземлился
                bool myPlaneLanded = false;

                // Извлечем структуры из словарей
                MyPlaneVisual myPlane = myPlaneVis[myKeys[i]]; // параметры полета данного самолета
                MyFlightHistory myOnePlaneHist = myPlanesHistory[myKeys[i]]; // история данного самолета
                int myPlaneHistCount = myOnePlaneHist.Time.Count; // число записей в истории данного самолета
                //myPlaneParameters myOnePlanePars = myAllPlanesPars[myKeys[i]];

                // Добавим новый самолет и уточним его запись в словаре myPlaneVis (добавим указатели на созданные объекты)
                if (!myPlane.GO) // в структуре еще нет указателя на самолет
                {
                    _Record.MyLog("ProcData", "%%% myFuncProcData(): Создадим новый самолет");
                    // Создадим новый самолет
                    Transform myNewPlane = Instantiate(mySamplePlane);
                    myNewPlane.name = myKeys[i];
                    myNewPlane.gameObject.SetActive(true);
                    myNewPlane.parent = myPlanesController;
                    // Указатель на вновь созданный Game Object
                    myPlane.GO = myNewPlane.gameObject;

                    // Указатели путевых точек
                    Transform[] myTP = new Transform[5];
                    for (int j = 0; j < 5; j++)
                    {
                        myTP[j] = Instantiate(myTrackPoint[j]);
                        myTP[j].name = myKeys[i] + "_Point_" + j;
                        myTP[j].parent = myTrackPoint[j].parent;
                        myTP[j].gameObject.SetActive(true);
                    }
                    myPlane.TrackPoints = myTP;

                    Transform myObjTr1 = myPlane.GO.transform.GetChild(0); // дочерний объект пока один
                    // Это канвас баннера с краткой информацией
                    myPlane.Banner1 = myObjTr1;
                    for (int k = 0; k < myObjTr1.childCount; k++)
                    {
                        Transform myObjTr2 = myObjTr1.GetChild(k);
                        switch (myObjTr2.name)
                        {
                            case "Call": // Позывной
                                myPlane.Banner1Call = myObjTr2.GetComponent<Text>();
                                myPlane.Banner1Call.text = myPlane.Call;
                                break;
                            case "Icao": // Код ИКАО
                                myPlane.Banner1Icao = myObjTr2.GetComponent<Text>();
                                myPlane.Banner1Icao.text = myPlane.Icao;
                                myPlane.Banner1Icao.gameObject.SetActive(myBanner1AddInfo); // Включить/выключить текстовое поле
                                break;
                            case "PReason": // Причина движения по прогнозу
                                myPlane.Banner1PReason = myObjTr2.GetComponent<Text>();
                                if(myPlane.BadPosCounter <= 1)
                                {
                                    myPlane.Banner1PReason.text = myPlane.PredictionReason;
                                }
                                else
                                {
                                    myPlane.Banner1PReason.text = myPlane.PredictionReason + myPlane.BadPosCounter;
                                }
                                myPlane.Banner1PReason.gameObject.SetActive(myBanner1AddInfo); // Включить/выключить текстовое поле
                                break;
                            case "Model":
                                myPlane.Banner1Model = myObjTr2.GetComponent<Text>(); // Текст второй строки баннера
                                String myText = MyFuncModelName(myAllPlanesPars[myKeys[i]].myModel);
                                if(myText.Length <= 12)
                                {
                                    myPlane.Banner1Model.fontSize = 75;
                                }
                                myPlane.Banner1Model.text = myText;// Модель
                                break;
                            case "Alt":
                                myPlane.Banner1Alt = myObjTr2.GetComponent<Text>(); // Текст третьей строки баннера
                                if (mySI)
                                {
                                    myPlane.Banner1Alt.text = "Alt(m)=" + myPlane.Position.y; // Высота в метрах
                                }
                                else
                                {
                                    myPlane.Banner1Alt.text = "Alt(ft)=" + myPlane.Position.y * myFeet; // Высота в футах
                                }
                                break;
                            case "Panel":
                                myPlane.Banner1Panel = myObjTr2.GetComponent<Image>(); // Фоновая картинка баннера
                                break;
                        }
                    }
                    // Сформируем имя 3D модели по ICAO кодам самолета и авиакомпании
                    String myModelName; // первая часть имени - модель самолета
                    if(myAllPlanesPars[myKeys[i]].myModel == null)
                    {
                        myModelName = "A320"; // Если код модели самолета пустой, то пусть будет A320
                    }
                    else // Если код модели самолета не пустой, то берем из имя словаря myKnownPlanes
                    {
                        if (myKnownPlanes.ContainsKey(myAllPlanesPars[myKeys[i]].myModel))
                        {
                            myModelName = myKnownPlanes[myAllPlanesPars[myKeys[i]].myModel];
                        }
                        else
                        {
                            myModelName = "A320"; // Если не знаем такой модели самолета, то тоже пусть будет A320
                        }
                    }

                    String myAirlineName; // вторая часть имени - название (сокращенное) авиакомпании, берем из словаря myKnownPlanes
                    if (myAllPlanesPars[myKeys[i]].myOperator == null)
                    {
                        myAirlineName = ""; // Если код авиакомпании пустой, то пусть название будет ""
                    }
                    else // Если код авиакомпании не пустой, берем из ее обозначение из словаря myKnownAirlines
                    {
                        if (myKnownAirlines.ContainsKey(myAllPlanesPars[myKeys[i]].myOperator))
                        {
                            myAirlineName = myKnownAirlines[myAllPlanesPars[myKeys[i]].myOperator];
                        }
                        else
                        {
                            myAirlineName = ""; // Если не знаем такую авиакомпанию, то обозначение тоже пусть будет ""
                        }
                    }

                    String my3DName; // Сформируем имя для 3D модели
                    if (myAirlineName != "") // если знаем такую авиакомпанию
                    {
                        String my3DNam = myModelName + "_" + myAirlineName; // предварительно имя 3D модели состоит из названия модели самолета и названия авиакомпании
                        // Проверим, есть ли 3D модель с таким именем
                        if (myPlanes3D.ContainsKey(my3DNam))
                        {
                            my3DName = my3DNam; // если есть - запоминаем в переменной my3DName
                        }
                        else
                        {
                            my3DName = myModelName; // если нет - имя 3D модели будет без только из названия самолета, без названия авиакомпании.
                        }
                    }
                    else // если не знаем авиакомпанию, имя 3D модели будет без только из названия самолета
                    {
                        my3DName = myModelName;
                    }

                    //Отладка
                    print("======= Код модели самолета: " + myAllPlanesPars[myKeys[i]].myModel + ", модель самолета: " + myModelName + ", код компании: " + myAllPlanesPars[myKeys[i]].myOperator + ", название компании: " + myAirlineName + ", имя 3D модели: " + my3DName);
                    //Еще раз проверим имя 3D модели
                    if (myPlanes3D.ContainsKey(my3DName))
                    {
                        print("======= Есть такая 3D модель!");
                    }
                    else
                    {
                        print("======= ОШИБКА! Нет такой 3D модели: " + my3DName);
                    }

                    // Создадим копию 3D модели
                    Transform myPlane3D = Instantiate(myPlanes3D[my3DName]);

                    // Переместим созданную 3D модель в дочерние объекты самолета
                    myPlane3D.parent = myPlane.GO.transform;
                    myPlane3D.localPosition = Vector3.zero;
                    // Запишем 3D модель (трансформ) в малую структуру
                    myPlane.Model = myPlane3D;

                    _Record.MyLog("ProcData", "%%% myFuncProcData(): Установим положение самолета");
                    myPlane.GO.transform.position = myPlane.Position;
                    myPlane.GO.transform.eulerAngles = myPlane.Euler;

                    // Уточним малую структуру в словаре
                    myPlaneVis[myKeys[i]] = myPlane;
                }
                // Удалим устаревший самолет и его записи во всех словарях. Также удалим самолет, если он "приземлися"
                else if (((_WebData.ResponseTime - myPlane.Time) > 15000))
                {
                    myPlaneToDelete = true;
                    _Record.MyLog("ProcData", myKeys[i], "%%% myFuncProcData(): Будем удалять устаревший самолет " + myKeys[i] + ". Время последнего приема данных от сервера = " + _WebData.ResponseTime + ", время поступления последних данных о самолете = " + myPlane.Time);
                }
                else
                {
                    // Проверим высоту по истории в последних точках. Если везде 0, то значит самолет приземлился
                    _Record.MyLog("ProcData", "%%% myFuncProcData(): Будем проверять, не сел ли самолет. Записей в истории всего " + myPlaneHistCount);
                    if (myPlaneHistCount < 3) // Недостаточно длинная история
                    {
                        _Record.MyLog("ProcData", "%%% myFuncProcData(): Записей недостаточно. По этому критерию - не сел");
                        myPlaneLanded = false;
                    }
                    else
                    {
                        myPlaneLanded = true;
                        for (int j = Mathf.Min(myPlaneHistCount, 7); j > 0; j--) // Здесь устанавливаем количество проверяемых точек
                        {
                            _Record.MyLog("ProcData", "%%% myFuncProcData(): Проверяем. Запись номер " + (myPlaneHistCount - j) + ": Высота = " + myOnePlaneHist.Position[myPlaneHistCount - j].y);
                            // Проверяем. Если высота хоть в одной точке больше 0 - значит не приземлился
                            if (myOnePlaneHist.Position[myPlaneHistCount - j].y > 0.0f)
                            {
                                myPlaneLanded = false;
                                break;
                            }
                        }
                        _Record.MyLog("ProcData", "%%% myFuncProcData(): Итого: myPlaneLanded = " + myPlaneLanded);
                    }
                    if (myPlaneLanded)
                    {
                        // Добавим запись о самолете в словарь приземлившихся
                        myLandedPlanes.Add(myKeys[i], myStartProcTime);
                        myPlaneToDelete = true;
                        _Record.MyLog("ProcData", myKeys[i], "%%% myFuncProcData(): Самолет " + myKeys[i] + " сел. Будем удалять ");
                    }
                }

                if (myPlaneToDelete) // Удаляем самолет и его записи во всех словарях
                {
                    _Record.MyLog("ProcData", "%%% myFuncProcData(): Удалим устаревший самолет. Время последнего начала последнего цикла обработки = " + myStartProcTime + ", время последних данных о самолете = " + myPlane.Time);
                    myAllPlanesPars.Remove(myKeys[i]); // Словарь - Большая структура
                    myPlaneVis.Remove(myKeys[i]); // Словарь - Малая структура
                    myPlanesHistory.Remove(myKeys[i]); // Словарь - История полета
                    if (mySlowPlanes.ContainsKey(myKeys[i])) // Словарь - "Медленные" самолеты
                    {
                        mySlowPlanes.Remove(myKeys[i]); // Удалим самолет из словаря медленных
                        mySlowPlanesCount = mySlowPlanes.Count; // Поправим счетчик медленных самолетов
                    }
                    // Проверим, не сидит ли у него случайно в дочерних объектах ступа с камерой.
                    for (int j = 0; j < myPlane.GO.transform.childCount; j++)
                    {
                        Transform myObjTr = myPlane.GO.transform.GetChild(j);
                        if (myObjTr.name == "Mortar") // Если сидит, то выведем ее в корень сцены
                        {
                            myObjTr.parent = null;
                        }
                    }
                    Destroy(myPlane.GO);
                    // Указатели путевых точек
                    for (int j = 0; j < 5; j++)
                    {
                        Destroy(myPlane.TrackPoints[j].gameObject);
                    }



                    // Закроем лог-файлы и удалим записи из словаря лог-файлов
                    _Record.Close(myKeys[i]);
                    _Record.Close(myKeys[i] + "_Data");

                }
                else // Самолет не удаляем
                {
                    // Есть ли у самолета скорость, достаточная для полета? (Пусть скорость будет выше 72 км/час = 20 метров/сек)
                    // Если меньше или равна - прячем самолет, если больше - показываем
                    // Также проверяем и корректируем словарь-список медленных самолетов

                    bool myLowSpeedDetected = myPlane.Speed.sqrMagnitude <= myLowSpeedSqr; // Сравниваем квадрат скорости
                    bool myPlaneAlreadyHasLowSpeed = mySlowPlanes.ContainsKey(myKeys[i]);

                    if(myLowSpeedDetected && myPlaneAlreadyHasLowSpeed) // Скорость низкая, как и была раньше
                    {
                        _Record.MyLog(myKeys[i], "ProcData", "%%% myFuncProcData(): ключ = " + myKeys[i] + ". Скорость низкая, как и была до того: самолет остается спрятанным");
                    }
                    else if(myLowSpeedDetected && !myPlaneAlreadyHasLowSpeed) // Обнаружена низкая скорость (ранее была или неизвестная, или высокая)
                    {
                        _Record.MyLog(myKeys[i], "ProcData", "%%% myFuncProcData(): ключ = " + myKeys[i] + ". Обнаружена низкая скорость: прячем самолет");
                        mySlowPlanes.Add(myKeys[i], false); // Добавим самолет в словарь медленных
                        mySlowPlanesCount = mySlowPlanes.Count; // Поправим счетчик медленных самолетов
                        myPlane.GO.SetActive(false); // Прячем самолет
                        for (int j = 0; j < 5; j++) // Указатели путевых точек
                        {
                            myPlane.TrackPoints[j].gameObject.SetActive(false);
                        }

                    }
                    else if (!myLowSpeedDetected && myPlaneAlreadyHasLowSpeed) // Обнаружена высокая скорость (ранее была низкая)
                    {
                        _Record.MyLog(myKeys[i], "ProcData", "%%% myFuncProcData(): ключ = " + myKeys[i] + ". Обнаружена высокая скорость после низкой: показываем самолет");
                        mySlowPlanes.Remove(myKeys[i]); // Удалим самолет из словаря медленных
                        mySlowPlanesCount = mySlowPlanes.Count; // Поправим счетчик медленных самолетов
                        myPlane.GO.SetActive(true); // Показываем самолет
                                                    // Указатели путевых точек
                        for (int j = 0; j < 5; j++)
                        {
                            myPlane.TrackPoints[j].gameObject.SetActive(true);
                        }
                    }
                    else  // Скорость высокая, как и была раньше
                    {
                        _Record.MyLog(myKeys[i], "ProcData", "%%% myFuncProcData(): ключ = " + myKeys[i] + ". Скорость высокая, как и была до того: самолет продолжает отображаться");
                    }

                }

                myPlane.Banner1PReason.text = myPlane.PredictionReason; // Отобразим на баннере самолета причину полета по прогнозу
                if(myPlane.PredictionReason == "Web")
                {
                    myPlane.Banner1Call.text = myPlane.Call;
                }
                else
                {
                    myPlane.Banner1Call.text = "." + myPlane.Call;
                }

               yield return new WaitForEndOfFrame();
            }

            // Отчитаемся о результатах

            mySceenMessage.text = "ВС в зоне: " + (myKeys.Count - mySlowPlanesCount);

            long myCurTime = _Time.CurrentTime();
            long myWorkTime = myCurTime - myStartProcTime2; // Время myStartProcTime2 равно myStartProcTime, за искоючением случая, когда новые данные не поступили, но прогноз еще не строим.
            _Record.MyLog("ProcData", "%%% myFuncProcData(): Всего самолетов в словарях: Большая структура = " + myAllPlanesPars.Count + ", малая структура =  " + myKeys.Count + ", из них скрытых (медленных) = " + mySlowPlanesCount);
            _Record.MyLog("RawData", "ProcData", "%%% myFuncProcData(): Завершение вторичной обработки. Время вторичной обработки = " + (myCurTime - mySecondProcStartTime) + " Время всей обработки = " + myWorkTime);


            // Переждать до конца рекомендованного времени цикла, секунд (если обработка заняла времени меньше)
            float myWaitTime = Mathf.Max(0.0f, (myProcCycleTime - myWorkTime / 1000.0f));
            _Record.MyLog("RawData", "ProcData", "%%% myFuncProcData(): Переждем еще " + myWaitTime + " секунд.");
            yield return new WaitForSeconds(myWaitTime);
            _Record.MyLog("RawData", "ProcData", "%%% myFuncProcData(): Переждали еще секунд: " + myWaitTime + " Разрешаю начать новый цикл обработки");

            mySecondaryDataProc = false;
            myPrimaryDataProc = true;

        }
    }



    // Продолжим заполнять большую структуру из объекта JSON
    // i - порядковый номер в JSON массиве acList[] (каждая запись соответствует данным одного самолета)
    // myKey - ключ текущего самолета
    private myPlaneParameters_OpenSky MyFuncBigStructure(myPlaneParameters_OpenSky myOnePlanePars, dynamic myJObj, int i, String myKey)
    {
        Type myPPType = typeof(myPlaneParameters_OpenSky); // тип объекта "myPlaneParameters"
        JToken myJPlanePars = myJObj.acList[i]; // данные одного самолета в исходном JSON

        // Строка для лога
        StringBuilder myLine = new StringBuilder(i + " key=" + myKey + " Icao=" + myOnePlanePars.icao24 + " Lat=" + myOnePlanePars.latitude + " Long=" + myOnePlanePars.longitude);
        StringBuilder myTabLine = new StringBuilder(myKey + "\t" + myOnePlanePars.icao24 + "\t" + myOnePlanePars.latitude + "\t" + myOnePlanePars.longitude + "\t" + myOnePlanePars.time_position);

        // Разберем данные по полям и заполним большую структуру
        foreach (JToken myChild in myJPlanePars.Children())
        {
            String myName = myChild.Path.Split(new Char[] { '.' })[1]; // имя поля в исходном JSON
            // Не выполнять для уже заполненных полей
            if ("Id,Icao,Lat,Long,CMsgs".Contains(myName))
            {
                continue;
            }
            Type myType = myPlaneParsType[myName]; // тип поля - находим по имени поля в словаре myPlaneParsType (описание полей большой структуры)
            String myValue = myChild.First.ToString(); // значение поля в исходном JSON
            System.Reflection.FieldInfo myFieldInfo = myPPType.GetField(myName); // метаинформация поля с именем myName из большой структуры параметров самолета

            // Преобразуем значения поля в исходном JSON в соответствии с его типом в большой структуре и запишем в соответствующее поле в большой структуре
            if (myType == typeof(int))
            {
                int myVal;
                if (Int32.TryParse(myValue, out myVal)) // преобразование типа к int
                {
                    myFieldInfo.SetValue(myOnePlanePars, myVal); // запись в поле экземпляра myOnePlanePars большой структуры
                }
                else
                {
                    _Record.MyLog("Ошибка преобразования данных перед записью в параметры большой структуры");
                }
            }
            else if (myType == typeof(bool)) // преобразование типа к bool 
            {
                bool myVal;
                if (Boolean.TryParse(myValue, out myVal))
                {
                    myFieldInfo.SetValue(myOnePlanePars, myVal); // запись в поле экземпляра myOnePlanePars большой структуры
                }
                else
                {
                    _Record.MyLog("Ошибка преобразования данных перед записью в параметры большой структуры");
                }
            }
            else if (myType == typeof(String)) // преобразование типа String не требуется
            {
                myFieldInfo.SetValue(myOnePlanePars, myValue); // запись в поле экземпляра myOnePlanePars большой структуры
            }
            else if (myType == typeof(float)) // преобразование типа к float
            {
                float myVal;
                if (Single.TryParse(myValue, out myVal))
                {
                    myFieldInfo.SetValue(myOnePlanePars, myVal); // запись в поле экземпляра myOnePlanePars большой структуры
                }
                else
                {
                    _Record.MyLog("Ошибка преобразования данных перед записью в параметры большой структуры");
                }
            }
            else if (myType == typeof(long)) // преобразование типа к long
            {
                long myVal;
                if (Int64.TryParse(myValue, out myVal))
                {
                    myFieldInfo.SetValue(myOnePlanePars, myVal); // запись в поле экземпляра myOnePlanePars большой структуры
                }
                else
                {
                    _Record.MyLog("Ошибка преобразования данных перед записью в параметры большой структуры");
                }
            }
            else if (myType == typeof(DateTime)) // преобразование типа к DateTime
            {
                DateTime myVal;
                if (DateTime.TryParse(myValue, out myVal))
                {
                    myFieldInfo.SetValue(myOnePlanePars, myVal); // запись в поле экземпляра myOnePlanePars большой структуры
                }
                else
                {
                    _Record.MyLog("Ошибка преобразования данных перед записью в параметры большой структуры");
                }
            }
            else
            {
                _Record.MyLog("Ошибка! Тип " + myType + " не описан в коде программы");
            }
            // Добавить имя и значение к концу строки для лога
            myLine.Append(" " + myName + "=" + myFieldInfo.GetValue(myOnePlanePars));
            myTabLine.Append("\t" + myFieldInfo.GetValue(myOnePlanePars));
        }
        // Записать строки в логи
        _Record.MyLog("ProcData", "=== myFuncThread()/MyFuncBigStructure(): " + myLine + "\t");
        _Record.MyLog(myKey + "_Data", myTabLine + "\t" + (_Time.CurrentTime()), false);
        return myOnePlanePars;
    }


    private void OnApplicationQuit()
    {
        // Закрыть все открытые лог-файлы
        _Record.CloseAll();
        // Закрыть фоновый поток
        myFightDataThread.Abort();
    }


    long myFrameCount = 0;
    int myLogFrameCount = 0;

    // Update is called once per frame
    void Update()
    {
        bool myAddInfoWasChaged = false; // Признак изменения дополнительной информации для отображения на баннере самолета с краткой информацией

        // Сначала чуть-чуть управления
        if (Input.GetKeyDown("-")) // Клавиша "минус": "Опустить" все самолеты на 10 метров (увеличить высоту аэропорта над уровнем моря)
        {
            myAirport_ALt += 10.0f;
            myWorldMessage.myFuncShowMessage("Самолеты ниже на 10 м.\nНовая высота Н.У.М. = " + myAirport_ALt, 3.0f);
        }
        else if (Input.GetKeyDown("=")) // Клавиша "плюс": "Поднять" все самолеты на 10 метров (уменьшить высоту аэропорта над уровнем моря)
        {
            myAirport_ALt -= 10.0f;
            myWorldMessage.myFuncShowMessage("Самолеты выше на 10 м.\nНовая высота Н.У.М. = " + myAirport_ALt, 3.0f);
        }
        else if (Input.GetKeyDown("i")) // Клавиша "i": Вывести / убрать с баннера самолета с краткой информацией дополнительную информацию
        {
            myBanner1AddInfo = !myBanner1AddInfo;
            myAddInfoWasChaged = true; // Пока только установим признак изменения, само изменение позже, в цикле обработки всех самолетов
        }
        else if (Input.GetKeyDown("c")) // Клавиша "с": Переключить метрическую систему СИ/футы-мили
        {
            mySI = !mySI;
        }

        // Основная часть - управление положением самолетов

        if (myPlaneVisValues != null)
        {
            // Текущее время сеанса
            long myCurTime = _Time.CurrentTime();

            // Отладка
            myFrameCount++; //
            bool myWriteLog = false; //
            float myDeltaY = 0.0f; //

            if(myLogFrameCount++ > 8)
            {
                myWriteLog = true;
                _Record.MyLog("Update", "===================== Frame=" + myFrameCount + " LogFrame=" + myLogFrameCount + " Time=" + myCurTime);
                myLogFrameCount = 0;
            }

            // Создаем список ключей из коллекции, чтобы не было ошибки InvalidOperationException: Collection was modified; enumeration operation may not execute (может быть изменена в фоновом потоке)
            List<String> myKeys = new List<String>(myPlaneVisKeys);

            // Двигаем самолеты
            foreach (String myKey in myKeys)
            {
                MyPlaneVisual myPlane = myPlaneVis[myKey]; // текущие параметры полета (малая структура)
                MyFlightHistory myOnePlaneHist = myPlanesHistory[myKey]; // история данного самолета
                int myPlaneHistCount = myOnePlaneHist.Time.Count; // число точек в истории
                long myLeftToTargetTime = myPlane.TargetTime - myCurTime; // сколько времени до прихода в последнюю путевую точку

                if (myPlane.GO)
                {

                    // Еше чуть-чуть управления
                    if (myAddInfoWasChaged) // Клавиша "i" была нажата: Вывести / убрать с баннера самолета с краткой информацией дополнительную информацию
                    {
                        // Включить/выключить текстовые поля
                        myPlane.Banner1Icao.gameObject.SetActive(myBanner1AddInfo);
                        myPlane.Banner1PReason.gameObject.SetActive(myBanner1AddInfo);
                    }

                    // Перемещение самолета
                    //myPlane.GO.transform.position = myPlane.GO.transform.position + myPlane.Speed * Time.deltaTime;

                    Vector3 myPos = myPlane.GO.transform.position;
                    Vector3 myNewPos;
                    Vector3 myEu = myPlane.GO.transform.eulerAngles;
                    Vector3 myNewEu;

                    // Производная от функции расчета положения по Безье (dZ/dX)
                    float myDerivative = 0.0f;
                    // Производные координат по времени для положения по Безье (dX/dt и dZ/dt)
                    float myDerX = 0.0f;
                    float myDerZ = 0.0f;
                    // Вторая производная от функции расчета положения по Безье (dZ2/d2X)
                    float myDer2 = 0.0f;
                    // Вторые производные координат от положения по Безье (d2X/dt2 и d2Z/dt2)
                    float myDer2Х = 0.0f;
                    float myDer2Z = 0.0f;

                    // Радиус кривизны траектории по Безье
                    float myCurvRadius = Mathf.Infinity;

                    if (myLeftToTargetTime > 0)
                    {

                        // Нормированное время (доля времени до прихода в конечную точку, меняется от 0 до 1)
                        float myTim=0.0f;

                        // Расчет новой точки на траектории

                        // Положение
                        if (myPlaneHistCount < 4) // если в истории меньше четырех точек - линейная интерполяция
                        {
                            myNewPos = myPos + (myPlane.Position - myPos) * Time.deltaTime * 1000 / myLeftToTargetTime;
                        }
                        else
                        {
                            // Нормированное время (доля времени до прихода в конечную точку, меняется от 0 до 1)
                            //float myTim;
                            if (myPlane.TrackTime != 0)
                            {
                                myTim = 1.0f - (float)(myPlane.PrevTargetTime - myCurTime) / myPlane.TrackTime;
                            }
                            else
                            {
                                myTim = 1.0f - (float)(myPlane.PrevTargetTime - myCurTime) / myLag;
                            }

                            //_Record.MyLog(myKey + "_Data", "Frame =\t" + myFrameCount + "\tmyCurTime =\t" + myCurTime + "\tmyLeftToTargetTime =\t" + myLeftToTargetTime + "\tmyPlane.TrackTime =\t" + myPlane.TrackTime + "\tmyPlane.TargetTime =\t" + myPlane.TargetTime + "\tmyPlane.PrevTargetTime =\t" + myPlane.PrevTargetTime + "\tTim =\t" + myTim, false);

                            if (myPlane.NeedNewBezier)
                            {

                                // =======================================================================
                                // Новая четверка путевых точек для полета по Безье. Начало установки
                                // =======================================================================
                                _Record.MyLog(myKey + "_Data", "===================== Frame=" + myFrameCount + " Новая четверка путевых точек для полета по Безье");

                                // Вычислим первую и вторую производные в текущей точке для кривой, построенной по старым путевым точкам

                                // Первые производные для X и Z по нормированному времени
                                float dX0 = myFuncDeriv_t(myKey, myPlane, myTim, 0);
                                float dZ0 = myFuncDeriv_t(myKey, myPlane, myTim, 2);
                                // Первая производная Z по X в текущей точке
                                float D1 = 0.0f;
                                if (dX0 != 0.0f) // Если dX0 не равен нулю
                                {
                                    D1 = dZ0 / dX0;
                                }
                                else if (dZ0 > 0.0f) // Если dX0 равен нулю, а dZ0 больше нуля
                                {
                                    D1 = Mathf.Infinity;
                                }
                                else if (dZ0 < 0.0f) // Если dX0 равен нулю, а dZ0 меньше нуля. Если dZ0 тоже равен нулю, то ничего не делаем: D1 будет равен нулю по определению переменной
                                {
                                    D1 = Mathf.NegativeInfinity;
                                }

                                // Вторые производные для X и Z по нормированному времени
                                float d2X0 = myFuncDeriv2_t(myPlane, myTim, 0);
                                float d2Z0 = myFuncDeriv2_t(myPlane, myTim, 2);
                                // Вторая производная Z по X в текущей точке
                                float D2 = 0.0f;
                                float myNumerator = (dX0 * d2Z0 - d2X0 *dZ0);
                                if (dX0 != 0.0f) // Если dX0 не равен нулю
                                {
                                    D2 = myNumerator / (dX0 * dX0 * dX0);
                                }
                                else if (myNumerator > 0.0f) // Если dX0 равен нулю, а числитель больше нуля
                                {
                                    D2 = Mathf.Infinity;
                                }
                                else if (myNumerator < 0.0f) // Если dX0 равен нулю, а числитель меньше нуля. Если числитель тоже равен нулю, то ничего не делаем: D2 будет равен нулю по определению переменной
                                {
                                    D2 = Mathf.NegativeInfinity;
                                }

                                _Record.MyLog(myKey + "_Data", "Tim =\t" + myTim + "\tdX0 =\t" + dX0 + "\tdZ0 =\t" + dZ0 + "\tD1 =\t" + D1 + "\td2X0 =\t" + d2X0 + "\td2Z0 =\t" + d2Z0 + "\tD2 =\t" + D2, false);

                                myPlane.StartBezier = myPos;
                                myPlane.PrevTargetTime = myPlane.TargetTime;
                                myPlane.TargetTime = myStartProcTime + myLag;
                                myPlane.TrackTime = myPlane.TargetTime - _Time.CurrentTime();
                                myLeftToTargetTime = myPlane.TrackTime;
                                myTim = 0.0f;
                                myPlane.NeedNewBezier = false;
                                myPlaneVis[myKey] = myPlane; // Уточним малую структуру в словаре

                                _Record.MyLog(myKey + "_Data", "Frame\tCurTime\tRealTime\tStartProcTime\tTargetTime\tTrackTime", false);
                                String myLine = myFrameCount + "\t" + myCurTime + "\t" + (_Time.CurrentTime()) + "\t" + 
                                    myStartProcTime + "\t" + myPlane.TargetTime + "\t" + myPlane.TrackTime;
                                _Record.MyLog(myKey + "_Data", myLine, false);
                                //_Record.MyLog(myKey + "_Data", "Положение самолета:" + myPlane.GO.transform.position + " myPos = " + myPos + " myPlane.StartBezier = " + myPlane.StartBezier + " myPlaneVis[myKey].StartBezier = " + myPlaneVis[myKey].StartBezier, false);


                                // Расставим указатели путевых точек для полета по Безье

                                // Первая путевая точка
                                myPlane.TrackPoints[0].position = myPlane.StartBezier;

                                // Вторая, третья и четвертая путевые точки
                                for (int j = 1; j < 4; j++)
                                {
                                    myPlane.TrackPoints[j].position = myOnePlaneHist.Position[myPlaneHistCount - 4 + j];
                                }

                                // Уточняем вторую путевую точка: X1,Z1. Найдем ее координаты из условий краевой задачи:
                                // 1. Кривая не должна иметь излома (равенство производных для кривых, построенных по старым и по новым путевым точкам)
                                // 2. Длина отрезка от первой точки до новой второй точки равна длине отрезка от первой точки до старой второй точки.

                                if (myPlaneHistCount >= 5)
                                {
                                    Vector2 myPoint0 = new Vector2(myPlane.TrackPoints[0].position.x, myPlane.TrackPoints[0].position.z);
                                    Vector2 myPoint1old = new Vector2(myPlane.TrackPoints[1].position.x, myPlane.TrackPoints[1].position.z);
                                    float myDist = Vector2.Distance(myPoint0, myPoint1old);
                                    float myNewAngle = (Mathf.Atan2(dZ0, dX0));
                                    Vector3 myPoint1new = new Vector3(myPoint0.x + myDist * Mathf.Cos(myNewAngle), myPlane.TrackPoints[1].position.y, myPoint0.y + myDist * Mathf.Sin(myNewAngle));

                                    myPlane.TrackPoints[4].position = myPlane.TrackPoints[1].position;
                                    myPlane.TrackPoints[1].position = myPoint1new;
                                }

                                _Record.MyLog(myKey + "_Data", "Точка\tPosX\tPosY\tPosZ", false);
                                for (int j = 0; j < 5; j++)
                                {
                                    myLine = j + "\t" + myPlane.TrackPoints[j].position.x + "\t" + myPlane.TrackPoints[j].position.y + "\t" + myPlane.TrackPoints[j].position.z;
                                    _Record.MyLog(myKey + "_Data", myLine, false);
                                }

                                // Найдем промежуточный целевой угол крена (на 1/4 пути по Безье, т.е. в окрестностях второй путевой точки)
                                // Впоследствии будем приближаться к этому углу линейной интерполяцией от текущего крена.

                                // Производные координат Безье по времени (dX/dt и dZ/dt) в точке 0.25
                                float myDX4 = myFuncDeriv_t(myKey, myPlane, 0.25f, 0);
                                float myDZ4 = myFuncDeriv_t(myKey, myPlane, 0.25f, 2);
                                // Вторые производные координат Безье по времени (d2X/dt2 и d2Z/dt2) в точке 0.25
                                float myD2Х4 = myFuncDeriv2_t(myPlane, 0.25f, 0);
                                float myD2Z4 = myFuncDeriv2_t(myPlane, 0.25f, 2);

                                // Кривизна (1/R) траектории по Безье в точке 0.25
                                float myTargetCurvature = 0;
                                float myDenominator = myDX4 * myDX4 + myDZ4 * myDZ4;

                                if (myDenominator != 0.0f)
                                {
                                    myTargetCurvature = (myDX4 * myD2Z4 - myD2Х4 * myDZ4) / Mathf.Pow(myDenominator, 1.5f);
                                }
                                else
                                {
                                    myTargetCurvature = Mathf.Infinity;
                                }

                                // Целевой угол крена в точке 0.25
                                float myTargetRoll = Mathf.Rad2Deg * Mathf.Atan(1000.0f * myTargetCurvature);
                                myTargetRoll = Mathf.Clamp(myTargetRoll, -30.0f, 30.0f);

                                // Присвоим этот угол крена объекту второй путевой точки
                                Vector3 myTargeEu = myPlane.TrackPoints[1].eulerAngles;
                                myTargeEu.z = myTargetRoll;
                                myPlane.TrackPoints[1].eulerAngles = myTargeEu;
                                // А текущий угол крена - объекту первой путевой точки
                                Vector3 mySourceEu = myPlane.TrackPoints[0].eulerAngles;
                                mySourceEu.z = myPlane.GO.transform.eulerAngles.z;
                                myPlane.TrackPoints[0].eulerAngles = mySourceEu;

                                _Record.MyLog(myKey + "_Data", "myDX4=\t" + myDX4 + "\tmyDZ4=\t" + myDZ4 + "\tmyD2Х4=\t" + myD2Х4 + "\tmyD2Z4=\t" + myD2Z4 +
                                    "\tmyDenominator=\t" + myDenominator + "\tmyTargetCurvature=\t" + myTargetCurvature + "\tmyTargetRoll\t" + myPlane.TrackPoints[1].eulerAngles.z + 
                                    "\tmySourceRoll\t" + myPlane.TrackPoints[0].eulerAngles.z, false);

                                // Заголовк для колонок данных (см. "Колонки данных под заголовком")
                                _Record.MyLog(myKey + "_Data", "Frame\tTim\tPosX\tPosY\tPosZ\tYaw\tdX/dt\tdZ/dt\tdZ/dX\td2X/dt2\td2Z/dt2\td2Z/dX2\tRadius\tRoll\tInter", false);


                                // =======================================================================
                                // Новая четверка путевых точек для полета по Безье. Установка завершена
                                // =======================================================================
                            }

                            // Положение по Безье
                            myNewPos = (1.0f - myTim) * (1.0f - myTim) * (1.0f - myTim) * myPlane.TrackPoints[0].position
                                + 3.0f * myTim * (1.0f - myTim) * (1.0f - myTim) * myPlane.TrackPoints[1].position
                                + 3.0f * myTim * myTim * (1.0f - myTim) * myPlane.TrackPoints[2].position
                                + myTim * myTim * myTim * myPlane.TrackPoints[3].position;

                            // Производные координат Безье по времени (dX/dt и dZ/dt)
                            myDerX = myFuncDeriv_t(myKey, myPlane, myTim, 0);
                            myDerZ = myFuncDeriv_t(myKey, myPlane, myTim, 2);

                            // Производная функции Безье (dZ/dX).

                            if (myDerX != 0) // Если myDerX не равен нулю
                            {
                                myDerivative = myDerZ / myDerX;
                            }
                            else if (myDerZ > 0.0f) // Если myDerX равен нулю, а myDerZ больше нуля
                            {
                                myDerivative = Mathf.Infinity;
                            }
                            else if (myDerZ < 0.0f) // Если myDerX равен нулю, а myDerZ меньше нуля. Если myDerZ тоже равен нулю, то ничего не делаем: myDerivative будет равен нулю по определению переменной
                            {
                                myDerivative = Mathf.NegativeInfinity;
                            }

                            // Вторые производные координат Безье по времени (d2X/dt2 и d2Z/dt2)
                            myDer2Х = myFuncDeriv2_t(myPlane, myTim, 0);
                            myDer2Z = myFuncDeriv2_t(myPlane, myTim, 2);

                            // Вторая производная функции Безье (d2Z/dX2)
                            float myNumerat = (myDerX * myDer2Z - myDer2Х * myDerZ);
                            if (myDerX != 0.0f) // Если myDerX не равен нулю
                            {
                                myDer2 = myNumerat / (myDerX * myDerX * myDerX);
                            }
                            else if (myNumerat > 0.0f) // Если myDerX равен нулю, а числитель больше нуля
                            {
                                myDer2 = Mathf.Infinity;
                            }
                            else if (myNumerat < 0.0f) // Если myDerX равен нулю, а числитель меньше нуля. Если числитель тоже равен нулю, то ничего не делаем: myDer2 будет равен нулю по определению переменной
                            {
                                myDer2 = Mathf.NegativeInfinity;
                            }


                            // Радиус кривизны траектории по Безье
                            if(myNumerat != 0.0f)
                            {
                                myCurvRadius = Mathf.Pow((myDerX * myDerX + myDerZ * myDerZ), 1.5f) / myNumerat;
                            }
                            else
                            {
                                myCurvRadius = Mathf.Infinity;
                            }

                            //_Record.MyLog(myKey + "_Data", "myTim = " + myTim + " myLeftToTargetTime = " + myLeftToTargetTime + " myPlane.TrackTime = " + myPlane.TrackTime + " myDerivative = " + myDerivative);
                        }
                        //_Record.MyLog(myKey + "_Data", "myPlane.position = " + myPlane.GO.transform.position);



                        if (myNewPos.y < 0)
                        {
                            myNewPos.y = 0;
                        }

                        // Поставим самолет в новую точку на траектории
                        myPlane.GO.transform.position = myNewPos;

                        // Угол
                        Vector3 myDeltaEu = (myPlane.Euler - myEu); // угол, на который нужно будет повернуть к концу периода

                        // Отладка
                        if (myWriteLog)
                        {
                            myDeltaY = myDeltaEu.y;
                        } //

                        // Приведем курсовой угол и угол тангажа от (0/360) к (-180/+180)
                        if (myDeltaEu.y > 180.0f)
                        {
                            myDeltaEu.y -= 360.0f;
                        }
                        else if (myDeltaEu.y < -180.0f)
                        {
                            myDeltaEu.y += 360.0f;
                        }
                        if (myDeltaEu.x > 180.0f)
                        {
                            myDeltaEu.x -= 360.0f;
                        }
                        else if (myDeltaEu.x < -180.0f)
                        {
                            myDeltaEu.x += 360.0f;
                        }

                        myPlane.GO.transform.eulerAngles = myEu + myDeltaEu * Time.deltaTime * 1000 / myLeftToTargetTime;

                        //// Отладка
                        //if (myWriteLog)
                        //{
                        //    _Record.MyLog("Update", "Plane=" + myKey + " OldEu=" + myEu.y + " TargetEu=" + myPlane.Euler.y + " DeltaEu=" + myDeltaY + " DeltaEuNorm=" + myDeltaEu.y + " NewEu=" + myPlane.GO.transform.eulerAngles.y);
                        //}

                        if (myPlaneHistCount >= 4) // если в истории больше четырех точек, то угол рысканья и угол крена заменим, исходя из траектории по Безье
                        {
                            // Рысканье (y)
                            string myInter = "Linear";
                            myNewEu = myPlane.GO.transform.eulerAngles;
                            float myDeltaX = myNewPos.x - myPos.x;
                            float myDeltaZ = myNewPos.z - myPos.z;
                            if (Math.Abs(myDeltaX) < 0.001f && Math.Abs(myDeltaZ) < 0.001f)
                            {
                                // Перемещение слишком мало, ничего не делаем
                            }
                            else if (myDeltaZ == 0.0f)
                            {
                                myNewEu.y = 90.0f * Mathf.Sign(myDeltaX);
                                myInter = "DeltaZ=0";
                            }
                            else if (myDeltaX == 0.0f)
                            {
                                myNewEu.y = 90.0f - 90.0f * Mathf.Sign(myDeltaZ);
                                myInter = "DeltaX=0";
                            }
                            else
                            {
                                //myNewEu.y = Mathf.Rad2Deg * (Mathf.Atan(myDeltaX / myDeltaZ));
                                //myNewEu.y = Mathf.Rad2Deg * (Mathf.Atan(1 / myDerivative));
                                myNewEu.y = Mathf.Rad2Deg * (Mathf.Atan2(myDerX, myDerZ));
                                myInter = "Bezier";
                            }

                            // Крен (z)
                            // Линейная интерполяция от угла крена в первой точке Безье до угла крена во второй точке
                            // Время ~1/4 общего времени движения по текущему отрезку кривой Безье
                            myNewEu.z = Mathf.LerpAngle(myPlane.TrackPoints[0].eulerAngles.z, myPlane.TrackPoints[1].eulerAngles.z, myTim * 4);

                            // Применим угол к модели самолета
                            myPlane.GO.transform.eulerAngles = myNewEu;

                            // Колонки данных под заголовком
                            _Record.MyLog(myKey + "_Data", myFrameCount + "\t" + myTim + "\t" + myPlane.GO.transform.position.x + "\t" + myPlane.GO.transform.position.y + "\t" + myPlane.GO.transform.position.z
                                + "\t" + myNewEu.y + "\t" + myDerX + "\t" + myDerZ + "\t" + myDerivative + "\t" + myDer2Х + "\t" + myDer2Z + "\t" + myDer2
                                + "\t" + myCurvRadius + "\t" + myNewEu.z + "\t" + myInter, false);
                        }
                    }

                    // Масштабирование модели самолета при приближении к земле
                    float myHeight = myPlane.GO.transform.position.y;
                    float myScale = 1.0f;
                    if ((myHeight < 1000.0f) || (myPlane.Model.localScale.x < 30.0f)) // высота, ниже которой начинается масшатбирование - 500 метров
                    {
                        myScale = Mathf.Clamp(((myHeight + 140.0f) / 38.0f), 5.0f, 30.0f);
                        myPlane.Model.localScale = myPlane.GO.transform.localScale * myScale;
                    }

                    // Ориентация баннера
                    myPlane.Banner1.LookAt(Camera.main.transform);
                    // Масштабирование баннера
                    //myDistance = Vector3.Distance(Camera.main.transform.position, myPlane.GO.transform.position);
                    float myDistance = (myPlane.GO.transform.position - Camera.main.transform.position).magnitude;
                    myScale = Mathf.Clamp(myDistance / 5000, 1.0f, 10.0f);
                    myPlane.Banner1.localScale = myPlane.GO.transform.localScale * myScale;
                    // Коррекция положения баннера относительно самолета
                    myPos = myPlane.Banner1.localPosition;
                    myPos.y = 180.0f * myScale + 180.0f;
                    myPlane.Banner1.localPosition = myPos;
                    // Текс баннера (третья строка - высота)
                    if (mySI)
                    {
                        myPlane.Banner1Alt.text = "Alt(m)=" + Math.Round(myPlane.GO.transform.position.y, 2).ToString("####0.00"); // Высота в метрах
                    }
                    else
                    {
                        myPlane.Banner1Alt.text = "Alt(ft)=" + (Math.Round(myPlane.GO.transform.position.y, 2) / myFeet).ToString("####0.00"); // Высота в футах
                    }
                    //myPlane.Banner1Text3.text = "Alt=" + Math.Round(myPlane.GO.transform.position.y, 2) + " Pitch=" + myPlane.GO.transform.eulerAngles.x;
                }
            }

            // Второй баннер с подробной информацией
            if (myBanner2.gameObject.activeInHierarchy && myPlaneVis.ContainsKey(mySelectedPlane))
            {
                myPlaneParameters_OpenSky mySelectedPlaneBigPars = myAllPlanesPars[mySelectedPlane];
                MyPlaneVisual mySelectedPlaneSmallPars = myPlaneVis[mySelectedPlane];

                if (mySI) // метрическая система СИ
                {
                    myBanner2Fields["Alt"].text = "Alt(m)=" + Math.Round(mySelectedPlaneSmallPars.GO.transform.position.y, 2).ToString("####0.00"); // Высота в метрах
                    myBanner2Fields["Speed"].text = "Speed(km/h)=" + Math.Round(mySelectedPlaneBigPars.velocity*3.6f, 2).ToString("####0.00"); // Скорость в км/час
                    myBanner2Fields["VSpd"].text = "VSpeed(m/sec)=" + Math.Round(mySelectedPlaneSmallPars.Speed.y, 2).ToString("####0.00"); // Вертикальная скорость в метр/сек
                }
                else // метрическая система футы/мили
                {
                    myBanner2Fields["Alt"].text = "Alt(ft)=" + (Math.Round(mySelectedPlaneSmallPars.GO.transform.position.y, 2) / myFeet).ToString("####0.00"); // Высота в футах
                    myBanner2Fields["Speed"].text = "Speed(kn)=" + Math.Round(mySelectedPlaneBigPars.velocity * 3.6f * myKnot, 2).ToString("####0.0"); // Скорость в узлах
                    myBanner2Fields["VSpd"].text = "VSpeed(ft/min)=" + Math.Round(mySelectedPlaneSmallPars.Speed.y/myFeet*60, 0).ToString(); // Вертикальная скорость в фут/мин
                }
                myBanner2Fields["Trak"].text = "Trak=" + Math.Round(mySelectedPlaneBigPars.true_track, 2).ToString("####0.0") + "°"; // Курс самолета в градусах
            }
        }
    }

    // Разобрать полное название модели и вернуть краткое
    String MyFuncModelName(String myLongName)
    {
        if (myLongName == null) // Модель не передана сервером
        {
            return "";
        }
        String myShortName;
        int myWordsNumber = 0;
        String[] myWords = myLongName.Split(new Char[] { ' ' }); // Извлечь список слов, разделенных пробелом

        switch (myWords[0])
        {
            case "Airbus":
            case "Boeing":
            case "Embraer":
            case "Ilyushin":
            case "Antonov":
                myWordsNumber = 2;
                break;
            case "Sukhoi":
            case "Bombardier":
                myWordsNumber = 3;
                break;
        }

        myWordsNumber = Math.Min(myWordsNumber, myWords.Length);

        if (myWordsNumber == 2)
        {
            myShortName = myWords[0] + " " + myWords[1];
        }
        else if (myWordsNumber == 3)
        {
            myShortName = myWords[0] + " " + myWords[1] + " " + myWords[2];
        }
        else // Название модели неизвестно, транслируем полностью
        {
            myShortName = myLongName;
        }
        return myShortName;
    }

    public void myFuncShowBanner2(String myKey)
    {
        // Выбранный самолет
        mySelectedPlane = myKey;
        // Большая структура данных выбранного самолета
        myPlaneParameters_OpenSky myOnePlanePars = myAllPlanesPars[mySelectedPlane];

        // Баннер с дополнительной информацией
        myBanner2Fields["Call"].text = "Позывной: " + myOnePlanePars.callsign;
        myBanner2Fields["Icao"].text = "Код ИКАО: " + myOnePlanePars.icao24;
        myBanner2Fields["Model"].text = "Модель ВС: " + myOnePlanePars.myModel;
        myBanner2Fields["Oper"].text = "Оператор: " + myOnePlanePars.myOperator;
        myBanner2Fields["From"].text = "From: " + myOnePlanePars.myFrom;
        myBanner2Fields["To"].text = "To: " + myOnePlanePars.myTo;

        myBanner2.gameObject.SetActive(true);

    }


    float myFuncDeriv_t(String myKey, MyPlaneVisual myPlane, float myTim, int myIndex)
    {
        float myDeriv = (-(1.0f - myTim) * (1.0f - myTim) * myPlane.TrackPoints[0].position[myIndex]
            + (1.0f - 4.0f * myTim + 3.0f * myTim * myTim) * myPlane.TrackPoints[1].position[myIndex]
            + (2.0f * myTim - 3.0f * myTim * myTim) * myPlane.TrackPoints[2].position[myIndex]
            + myTim * myTim * myPlane.TrackPoints[3].position[myIndex]) * 3.0f;
//        _Record.MyLog(myKey + "_Data", "myIndex=\t" + myIndex + "\tmyTim=\t" + myTim +
//            "\tP0=\t" + myPlane.TrackPoints[0].position[myIndex] + "\tK0=\t" + -(1.0f - myTim) * (1.0f - myTim) + "\tS0=\t" + -(1.0f - myTim) * (1.0f - myTim) * myPlane.TrackPoints[0].position[myIndex] +
//            "\tP1=\t" + myPlane.TrackPoints[1].position[myIndex] + "\tK1=\t" + (1.0f - 4.0f * myTim + 3.0f * myTim * myTim) + "\tS1=\t" + (1.0f - 4.0f * myTim + 3.0f * myTim * myTim) * myPlane.TrackPoints[1].position[myIndex] +
//            "\tP2=\t" + myPlane.TrackPoints[2].position[myIndex] + "\tK2=\t" + (2.0f * myTim - 3.0f * myTim * myTim) + "\tS2=\t" + (2.0f * myTim - 3.0f * myTim * myTim) * myPlane.TrackPoints[2].position[myIndex] +
//            "\tP3=\t" + myPlane.TrackPoints[3].position[myIndex] + "\tK3=\t" + myTim * myTim + "\tS3=\t" + myTim * myTim * myPlane.TrackPoints[3].position[myIndex], false);
        return myDeriv;
    }

    float myFuncDeriv2_t(MyPlaneVisual myPlane, float myTim, int myIndex)
    {
        float myDeriv2 = ((1.0f - myTim) * myPlane.TrackPoints[0].position[myIndex]
            - (2.0f - myTim * 3.0f) * myPlane.TrackPoints[1].position[myIndex]
            + (1.0f - myTim * 3.0f) * myPlane.TrackPoints[2].position[myIndex]
            + myTim * myPlane.TrackPoints[3].position[myIndex]) * 6.0f;
        return myDeriv2;
    }


}
