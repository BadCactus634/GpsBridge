# GpsBridge Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** App Windows che porta il flusso del GPS u-blox M10 (USB-seriale) a Google Earth su una COM virtuale a 4800, configura tutte le costellazioni, registra GPX e mostra mappa, diagnostica e impostazioni.

**Architecture:** Libreria `GpsBridge.Core` (senza UI, testata con xUnit e un NMEA di prova riprodotto da file) + app WPF `GpsBridge.App` sopra. Flusso: SerialSource -> FrameSplitter (NMEA+UBX) -> bus che alimenta EarthWriter, GpxRecorder, GnssState (diagnostica/mappa). UbxConfigurator parla col ricevitore sullo stesso flusso.

**Tech Stack:** .NET 8, WPF, WebView2 + Leaflet, xUnit, System.IO.Ports, Inno Setup, com0com (firmato).

**Spec:** `docs/spec-design.md`

## Global Constraints
- Lingua UI e messaggi: italiano.
- Baud da provare in autorilevamento: 4800, 9600, 38400, 115200, 230400. Baud della COM virtuale per Earth: 4800 (parametro).
- Costellazioni: GPS, Galileo, GLONASS, BeiDou **B1C** (mai B1I insieme a GLONASS), QZSS/SBAS dove ammesso; su NAK si scende per priorita' e si mostra lo stato reale.
- Uscita verso Earth: talker riscritti a `GP`, checksum ricalcolato, frasi GGA/RMC/GSA/GSV/VTG/GLL, 1 Hz di default. GPX e mappa a frequenza piena (fino a 10 Hz).
- Configurazione ricevitore: scritta in RAM+BBR+flash di default (utente), ma solo se VALGET mostra differenze dal profilo.
- Coppia com0com predefinita COM20<->COM21 (app scrive su COM20, Earth apre COM21).
- Tile OSM: User-Agent proprio, niente scaricamento massivo.
- Progetto fuori dal repo EnjoyBuds (`Documents\GpsBridge`); commit locali frequenti.
- Mia scelta rispetto alla spec: parser NMEA **proprio** (GGA/RMC/GSA/GSV, ~150 righe) invece di NmeaParser NuGet, perche' servono comunque le frasi grezze per la riscrittura dei talker e le classi di NmeaParser non le espongono comodamente.

## Review Focus
1. Frase NMEA troncata o con checksum errato a meta' flusso: scartata, contata negli errori, mai inoltrata a Earth.
2. Byte UBX binari mescolati all'NMEA (risposte ACK/NAK): non rompono il framing NMEA ne' finiscono sulla COM virtuale.
3. Cavo USB staccato durante la registrazione: GPX resta valido, riconnessione automatica, COM virtuale viva.
4. Nessun fix (campi GGA/RMC vuoti, `V`): niente punti nel GPX e niente posizione a (0,0) sulla mappa.
5. Coppia com0com assente o COM21 occupata: errore chiaro, il resto (mappa/GPX/diagnostica) funziona lo stesso.

---

## File structure
```
GpsBridge.sln
src/GpsBridge.Core/
  Nmea/NmeaSentence.cs        parse + checksum + talker rewrite
  Nmea/FrameSplitter.cs       separa NMEA e UBX in un flusso di byte
  Nmea/GnssState.cs           GGA/RMC/GSA/GSV -> stato (fix, DOP, satelliti)
  Ubx/UbxFrame.cs             costruzione/lettura frame, checksum Fletcher
  Ubx/UbxKeys.cs              chiavi CFG-* e profilo
  Ubx/UbxConfigurator.cs      MON-VER, VALGET, VALSET, ACK/NAK, fallback
  Io/IByteStream.cs           astrazione porta (reale, file di prova, finta)
  Io/SerialSource.cs          autobaud + riconnessione
  Io/EarthWriter.cs           filtro/riscrittura/limite 1 Hz -> COM virtuale
  Io/Com0comSetup.cs          rileva/installa la coppia
  Gpx/GpxRecorder.cs          scrittura incrementale, riparazione, filtro fermi
  Settings/AppSettings.cs     JSON in %AppData%\GpsBridge
src/GpsBridge.App/            WPF: MainWindow, schede Mappa/Diagnostica/Traccia/Impostazioni
tests/GpsBridge.Core.Tests/   xUnit + tests/data/sample-multi-gnss.nmea
installer/GpsBridge.iss       Inno Setup
```

### Task 1: Scaffold e frase NMEA

**Files:** Create `GpsBridge.sln`, i tre progetti, `Nmea/NmeaSentence.cs`; Test `NmeaSentenceTests.cs`.

**Interfaces:**
- Produces: `record NmeaSentence(string Talker, string Type, string[] Fields, string Raw)`; `static bool NmeaSentence.TryParse(string line, out NmeaSentence s)` (valida `$...*HH`); `static string NmeaSentence.Checksum(string body)` (hex 2 cifre maiuscole); `string NmeaSentence.WithTalker(string talker)` (rifa la riga col nuovo talker e checksum, con `\r\n`).

- [ ] **Step 1:** Test: `TryParse("$GNGGA,...*HH")` con checksum giusto -> true, `Talker=="GN"`, `Type=="GGA"`; checksum sbagliato -> false; riga senza `*` -> false; `WithTalker("GP")` su `$GBGSV,...` restituisce riga che a sua volta passa `TryParse` con `Talker=="GP"`.
- [ ] **Step 2:** `dotnet test` -> FAIL (tipi mancanti).
- [ ] **Step 3:** Creare soluzione e progetti (`net8.0`, `net8.0-windows` per l'app, xUnit) e implementare i metodi sopra.
- [ ] **Step 4:** `dotnet test` -> PASS.
- [ ] **Step 5:** `git init`, `.gitignore` per .NET, commit «scaffold e NmeaSentence».

### Task 2: FrameSplitter (NMEA + UBX nello stesso flusso)

**Files:** Create `Nmea/FrameSplitter.cs`; Test `FrameSplitterTests.cs`.

**Interfaces:**
- Consumes: `NmeaSentence.TryParse`.
- Produces: `class FrameSplitter { event Action<NmeaSentence> Nmea; event Action<byte[]> Ubx; event Action<string> BadLine; void Feed(ReadOnlySpan<byte> data); }` (`Ubx` riceve il frame completo `B5 62 ... ck_a ck_b`).

- [ ] **Step 1:** Test: dati spezzati a byte arbitrari producono le stesse frasi; un frame UBX (`B5 62 05 01 ...`) tra due frasi NMEA genera un evento `Ubx` e non danneggia le frasi vicine (Review Focus 2); una riga con checksum errato genera `BadLine` e non `Nmea` (Focus 1); byte spazzatura prima di `$` ignorati.
- [ ] **Step 2:** FAIL. **Step 3:** Implementare con macchina a stati (cerca `$` o `0xB5 0x62`; per UBX legge lunghezza dai byte 4-5 e verifica Fletcher; per NMEA fino a `\n`, limite 120 caratteri). **Step 4:** PASS. **Step 5:** Commit.

### Task 3: GnssState (diagnostica da NMEA)

**Files:** Create `Nmea/GnssState.cs`; Test `GnssStateTests.cs`, dato `tests/data/sample-multi-gnss.nmea` (GGA/RMC/GSA/GSV con talker GN/GP/GA/GB/GL, una GGA senza fix).

**Interfaces:**
- Consumes: `NmeaSentence`.
- Produces: `class GnssState { void Apply(NmeaSentence s); GnssFix? Position {get;} (null senza fix); int FixQuality; double? Hdop,Pdop,Vdop; IReadOnlyList<SatInfo> Satellites; IReadOnlyDictionary<string,(int Used,int InView)> PerConstellation; event Action Changed; }`; `record GnssFix(double Lat,double Lon,double? Alt,DateTimeOffset Utc,double? SpeedKnots,double? CourseDeg)`; `record SatInfo(string Constellation,int Prn,int? Elev,int? Azim,int? Snr,bool Used)`.

- [ ] **Step 1:** Test sul file di prova: lat/lon in gradi decimali con segno (N/S, E/W), ora UTC da RMC+data, satelliti raggruppati per talker (GP=GPS, GA=Galileo, GB=BeiDou, GL=GLONASS, GQ=QZSS), GSV multi-messaggio composto da piu' frasi, `Position==null` quando GGA ha qualita' 0 o RMC e' `V` (Focus 4).
- [ ] **Step 2-4:** FAIL, implementare, PASS. **Step 5:** Commit.

### Task 4: Frame UBX e chiavi di configurazione

**Files:** Create `Ubx/UbxFrame.cs`, `Ubx/UbxKeys.cs`; Test `UbxFrameTests.cs`.

**Interfaces:**
- Produces: `static byte[] UbxFrame.Build(byte cls,byte id,byte[] payload)`; `static bool UbxFrame.TryRead(byte[] frame,out byte cls,out byte id,out byte[] payload)`; `static byte[] UbxFrame.ValSet(byte layers,IEnumerable<(uint Key,ulong Value)> items)` (layers: RAM=1, BBR=2, Flash=4; la dimensione del valore si ricava dal byte `size` della chiave, bit 28-30); `static byte[] UbxFrame.ValGet(byte layer,IEnumerable<uint> keys)`; `static byte[] UbxFrame.MonVer()`. `UbxKeys` e il profilo `ReceiverProfile` con le chiavi: CFG-SIGNAL-{GPS,GAL,BDS,GLO,QZSS,SBAS}_ENA e sottosegnali (GPS L1CA, GAL E1, BDS B1C **on** / B1 **off**, GLO L1, QZSS L1CA), CFG-RATE-MEAS, CFG-NAVSPG-DYNMODEL, CFG-UART1-BAUDRATE, CFG-UART1OUTPROT-NMEA, CFG-MSGOUT-NMEA_ID_{GGA,GLL,GSA,GSV,RMC,VTG}_UART1.

- [ ] **Step 1:** Test con vettori noti: `MonVer()` == `B5 62 0A 04 00 00 0E 34`; `Build`+`TryRead` fanno il giro; `ValSet` con una chiave U1 e una U4 produce lunghezza e checksum corretti.
- [ ] **Step 2-4:** FAIL, implementare (Fletcher-8 su classe..payload), PASS.
- [ ] **Step 3b:** Gli ID numerici delle chiavi vanno **verificati sull'interface description M10 (UBX-20048810) o in u-center** prima di fidarsene: copiarli da li' nel file con il riferimento in commento. Nota: non sono confermati nella spec.
- [ ] **Step 5:** Commit.

### Task 5: UbxConfigurator

**Files:** Create `Ubx/UbxConfigurator.cs`; Test `UbxConfiguratorTests.cs` con un `FakeReceiver` che implementa `IByteStream`.

**Interfaces:**
- Consumes: `UbxFrame`, `ReceiverProfile`, `IByteStream` (Task 6; per i test qui si definisce prima `Io/IByteStream.cs`: `interface IByteStream { void Write(ReadOnlySpan<byte>); event Action<byte[]> DataReceived; }`).
- Produces: `Task<string> ReadVersionAsync()` (SW/HW/estensioni da MON-VER); `Task<ConfigDiff> DiffAsync(ReceiverProfile p)` (VALGET vs profilo); `Task<ApplyResult> ApplyAsync(ReceiverProfile p, byte layers)` con fallback delle costellazioni su NAK in ordine GPS, Galileo, BeiDou B1C, GLONASS, QZSS, SBAS e rapporto `ActiveConstellations`; scrive solo se `Diff` non e' vuoto.

- [ ] **Step 1:** Test col finto ricevitore: diff vuoto -> nessun VALSET inviato (Review: non consumare flash); NAK sulla combinazione completa -> secondo tentativo senza GLONASS e rapporto che dice cosa manca; nessuna risposta entro il timeout (2 s) -> errore chiaro, non blocco.
- [ ] **Step 2-4:** FAIL, implementare con attesa ACK/NAK (UBX-ACK 0x05 0x01/0x00), PASS. **Step 5:** Commit.

### Task 6: SerialSource (autobaud, riconnessione, file di prova)

**Files:** Create `Io/SerialSource.cs`, `Io/ReplayStream.cs`; Test `SerialSourceTests.cs`.

**Interfaces:**
- Produces: `class SerialSource : IByteStream, IAsyncDisposable { SerialSource(Func<string,int,IPort> opener, SettingsSnapshot s); Task StartAsync(CancellationToken ct); event Action<ConnState> StateChanged; int DetectedBaud; }` con `IPort` astrazione di `SerialPort`; `ReplayStream` riproduce un `.nmea` per prove senza hardware.
- Autobaud: per ogni baud prova ~1,5 s e accetta se riceve almeno 2 frasi con checksum valido; riconnessione con backoff 1-2-5-10 s (Focus 3).

- [ ] **Step 1:** Test con `IPort` finto: lo stesso flusso valido solo a 38400 -> `DetectedBaud==38400`; la porta che lancia IOException a meta' -> stato `Disconnected`, poi `Connected` al ritorno, senza eccezioni all'esterno.
- [ ] **Step 2-4:** FAIL, implementare, PASS. **Step 5:** Commit.

### Task 7: EarthWriter e com0com

**Files:** Create `Io/EarthWriter.cs`, `Io/Com0comSetup.cs`; Test `EarthWriterTests.cs`.

**Interfaces:**
- Consumes: `NmeaSentence.WithTalker`.
- Produces: `class EarthWriter { EarthWriter(IByteStream outPort, double hz=1); void OnSentence(NmeaSentence s); EarthStatus Status; }` (passa solo GGA/RMC/GSA/GSV/VTG/GLL, talker -> `GP`, un ciclo al secondo: GGA e RMC al massimo 1/s, le altre frasi dello stesso ciclo vanno con loro); `Com0comSetup.IsInstalled()`, `Com0comSetup.EnsurePair(string a="COM20", string b="COM21")` (usa `setupc.exe`, richiede UAC, errore chiaro se manca; Focus 5).

- [ ] **Step 1:** Test: ingresso 10 Hz -> uscita 1 GGA/s; ingresso `$GNGGA` -> uscita `$GPGGA` con checksum valido; frasi non in elenco (es. `TXT`) scartate; porta di uscita che lancia errore -> `Status` dice «COM virtuale non disponibile» e il resto non si ferma.
- [ ] **Step 2-4:** FAIL, implementare, PASS. **Step 5:** Commit.

### Task 8: GpxRecorder

**Files:** Create `Gpx/GpxRecorder.cs`; Test `GpxRecorderTests.cs`.

**Interfaces:**
- Consumes: `GnssFix`, `GnssState`.
- Produces: `class GpxRecorder { GpxRecorder(string dir, double minMoveMeters=3, double minSpeedKnots=0.3); void Start(); void Stop(); void OnFix(GnssFix f, int sats, double? hdop); static void RepairIfOpen(string path); bool IsRecording; string? CurrentFile; }`. GPX 1.1, `<trkpt lat lon><ele><time><sat><hdop><fix>`, flush a ogni punto con chiusura dei tag riscritta in coda.

- [ ] **Step 1:** Test: punti fermi (entro `minMoveMeters`) scartati; file dopo `Stop()` valido XML con N trkpt; file troncato senza `</gpx>` -> `RepairIfOpen` lo rende valido; nessun punto se `OnFix` non e' chiamato (senza fix, Focus 4).
- [ ] **Step 2-4:** FAIL, implementare, PASS. **Step 5:** Commit.

### Task 9: AppSettings

**Files:** Create `Settings/AppSettings.cs`; Test `AppSettingsTests.cs`.

**Interfaces:** `class AppSettings { string? RealPort; int? RealBaud; string EarthPort="COM20"; int EarthBaud=4800; double EarthHz=1; string GpxDir; double MinMoveMeters; bool AutoStartBridge, AutoStartRecording, StartWithWindows; ReceiverProfile Receiver; static AppSettings Load(string path); void Save(string path); }` JSON in `%AppData%\GpsBridge\settings.json`; file mancante o corrotto -> predefiniti, mai crash.
`ReceiverProfile.Best` = profilo migliore: tutte le costellazioni ammesse, 5 Hz, modello dinamico portatile, NMEA 4.10 con GGA/RMC/GSA/GSV/VTG/GLL, baud 115200 (da confermare sul ricevitore in Task 13).

- [ ] **Step 1:** Test giro Save/Load, file corrotto -> predefiniti. **Step 2-5:** FAIL, implementare, PASS, commit.

### Task 10: Guscio WPF e collegamento dei pezzi

**Files:** Create `src/GpsBridge.App/` (`App.xaml`, `MainWindow.xaml`, `BridgeService.cs`).

**Interfaces:** `BridgeService` possiede `SerialSource -> FrameSplitter -> {EarthWriter, GpxRecorder, GnssState}` e `UbxConfigurator`; espone `Start()/Stop()`, stato, eventi. Barra di stato con porta, baud, fix, COM virtuale, registrazione. Prova senza hardware: flag di avvio `--replay file.nmea`.

- [ ] **Step 1:** Test di integrazione in Core con `ReplayStream`: il file di prova produce frasi `GP` su un `IByteStream` finto, un GPX valido e `GnssState` con posizione.
- [ ] **Step 2-4:** FAIL, scrivere `BridgeService` (in Core, testabile) e il guscio WPF che lo usa, PASS. **Step 5:** commit.

### Task 11: Scheda Diagnostica

**Files:** Create `Views/DiagnosticsView.xaml(.cs)`, `ViewModels/DiagnosticsViewModel.cs`.

Mostra: tipo di fix, HDOP/PDOP/VDOP, satelliti usati/visti per costellazione, barre SNR per satellite, skyplot (azimut/elevazione), frasi/s, errori di checksum, riconnessioni, versione firmware e costellazioni attive (da `ReadVersionAsync`/`ApplyAsync`).

- [ ] **Step 1:** Test sul view model (puro, senza WPF): da `GnssState` del file di prova produce `ConstellationRows`, `SkyPoints` (x,y normalizzati da azimut/elevazione) e i contatori.
- [ ] **Step 2-4:** FAIL, implementare VM e vista, PASS; **verifica a vista** lanciando l'app con `--replay`. **Step 5:** commit.

### Task 12: Scheda Mappa e cache tile

**Files:** Create `Views/MapView.xaml(.cs)`, `Map/TileCache.cs`, `Map/map.html` (Leaflet in locale), Test `TileCacheTests.cs`.

**Interfaces:** `class TileCache { TileCache(string dir, long maxBytes, Func<string,Task<byte[]>> fetch); Task<byte[]?> GetAsync(int z,int x,int y); }` cache su disco con rimozione dei piu' vecchi oltre `maxBytes`; il fetch usa User-Agent proprio e non prescarica (Global Constraints). WebView2 serve le tile da `https://tiles.local/{z}/{x}/{y}.png` tramite `WebResourceRequested`. La mappa riceve posizione e traccia via `PostWebMessageAsJson`; nessun marker finche' `Position==null`.

- [ ] **Step 1:** Test: seconda richiesta della stessa tile non richiama `fetch`; oltre `maxBytes` la tile piu' vecchia sparisce; `fetch` che fallisce senza cache -> `null`, con cache -> dati vecchi.
- [ ] **Step 2-4:** FAIL, implementare, PASS; verifica a vista con `--replay`. **Step 5:** commit.

### Task 13: Scheda Impostazioni (programma + ricevitore) e prova sul ricevitore

**Files:** Create `Views/SettingsView.xaml(.cs)`, `ViewModels/SettingsViewModel.cs`.

Due sezioni come in spec. Programma: porta reale/baud (auto o fissi), COM virtuale + baud + Hz verso Earth, cartella GPX, filtro fermi, cache tile (dimensione, svuota), avvio con Windows, avvio automatico di ponte e registrazione. Ricevitore: costellazioni/segnali, frequenza 1-10 Hz, modello dinamico, frasi NMEA, baud UART, SBAS, stato letto dal ricevitore, pulsanti «Applica profilo migliore» (RAM+BBR+flash), «Leggi dal ricevitore», «Salva in flash», «Ripristina predefiniti u-blox».

- [ ] **Step 1:** Test sul view model: modificare un campo e salvare aggiorna `AppSettings`; «Applica profilo migliore» chiama `ApplyAsync(Best, 7)` (RAM|BBR|Flash) su un finto configuratore; con diff vuoto mostra «gia' a posto».
- [ ] **Step 2-4:** FAIL, implementare, PASS.
- [ ] **Step 5 (serve il ricevitore, l'utente lo collega):** prova di fumo guidata: `ReadVersionAsync`, verificare gli ID chiave del Task 4 con VALGET, applicare il profilo migliore, controllare nella Diagnostica le 4 costellazioni; se qualche chiave e' rifiutata correggerla in `UbxKeys.cs`. Commit con i valori confermati.

### Task 14: Installer e prova con Google Earth

**Files:** Create `installer/GpsBridge.iss`, `installer/README.md`.

`dotnet publish -c Release -r win-x64 --self-contained` + Inno Setup: copia l'app, include l'installer firmato di com0com e al termine esegue `setupc.exe install PortName=COM20 PortName=COM21` con UAC; scorciatoia, disinstallazione che rimuove la coppia.

- [ ] **Step 1:** Compilare l'installer, provarlo su questo PC e verificare che `COM20`/`COM21` compaiano in Gestione dispositivi.
- [ ] **Step 2:** Con `--replay`, aprire Google Earth Pro -> Strumenti -> GPS -> Tempo reale, porta COM21, 4800, NMEA: il puntatore deve seguire il file. Poi con il ricevitore vero.
- [ ] **Step 3:** Commit e nota nel README di cosa e' stato provato davvero.

---

## Self-review
- **Copertura spec:** ponte e autobaud (6,7,10), costellazioni+fallback+flash (4,5,13), GPX (8), mappa+offline (12), diagnostica (3,11), impostazioni doppie (9,13), installer+com0com (7,14), riavvio/offline/taglio (5,6,8, Review Focus). Nessun buco.
- **Deviazione dalla spec:** parser NMEA proprio invece di NmeaParser (dichiarata sopra).
- **Rischio aperto:** gli ID delle chiavi CFG-* e il «profilo migliore» sono provvisori fino alla prova sul ricevitore (Task 4 step 3b, Task 13 step 5).
