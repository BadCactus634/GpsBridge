# GpsBridge — design

Data: 2026-11-16. App Windows personale per un ricevitore u-blox M10 (DIYmalls GM10, USB-seriale).

## Perche' (intento)
Google Earth Pro legge il GPS da una porta COM a 4800 baud. Il GM10 a 4800 e' instabile e il suo NMEA con piu' costellazioni (`$GN`, `$GA`, `$GB`) rischia di non essere capito da Earth. L'app sta in mezzo: parla col ricevitore alla sua velocita' vera, configura tutte le costellazioni, e passa a Earth un NMEA «pulito» su una porta COM virtuale a 4800 (parametro che sulle porte virtuali non limita nulla). In piu' registra GPX, mostra mappa e diagnostica.

## Obiettivi
1. Ponte: porta reale (autorilevamento baud: 4800/9600/38400/115200/230400) -> porta COM virtuale per Google Earth.
2. Sblocco satelliti: GPS + Galileo + GLONASS + BeiDou (B1C, non B1I: B1I e GLONASS non convivono) + QZSS/SBAS dove ammesso, via UBX-CFG-VALSET (chiavi CFG-SIGNAL-*). Se la combinazione e' rifiutata (NAK) si scende per ordine di priorita' e si mostra cosa e' attivo davvero. Lettura versione firmware con UBX-MON-VER.
3. Registrazione di una traccia GPX (start/stop, scrittura incrementale, nessuna perdita se l'app si chiude male).
4. Mappa (OSM) con posizione live e traccia.
5. Diagnostica: tipo di fix, HDOP/PDOP/VDOP, satelliti usati/visti per costellazione, SNR per satellite, skyplot, frequenza frasi, errori di checksum, riconnessioni.

## Non obiettivi
Niente flash firmware, niente multiutente o cloud, niente modifica del ricevitore oltre a costellazioni, frequenza e baud. Non e' EnjoyBuds: progetto a parte.

## Approcci considerati (scelta dell'utente 16/11)
- **Stack**: C# .NET + WPF (scelto). Scartati Python+PySide6 (eseguibile pesante, mappa scomoda) e Tauri (piu' codice per la seriale).
- **Porta virtuale**: com0com (scelto), incluso nell'installer perche' il software e' personale e si vuole un solo file. Scartato NMEA su TCP/UDP: Google Earth Pro vuole una COM.
- **Mappa**: Leaflet in WebView2 con tile OSM e cache su disco per l'offline (scelto). Scartati solo-online e nessuna mappa.
- **Installer**: Inno Setup, un solo `.exe`; installa l'app (publish self-contained) e il driver com0com firmato con `setupc.exe install PortName=COM20 PortName=COM21` (UAC una volta sola). Mia scelta: nessuna scelta dell'utente su questo.

## Architettura
Un nucleo senza interfaccia (testabile) e una UI sopra.

```
SerialSource (porta reale, autobaud, riconnessione)
  -> LineSplitter (NMEA + UBX nello stesso flusso)
     -> NmeaBus (parse + checksum)
         |-> EarthWriter   (riscrive talker GN/GA/GB/GL -> GP, ricalcola checksum, filtra GGA/RMC/GSA/GSV/VTG/GLL,
         |                  limita a 1 Hz opzionale, scrive sulla COM virtuale)
         |-> GpxRecorder   (trkpt: lat, lon, ele, time, sat, hdop, fix; filtro punti fermi)
         |-> DiagnosticsModel (stato fix, DOP, satelliti per costellazione, SNR, statistiche)
         |-> MapViewModel  (posizione + traccia -> Leaflet via WebView2)
UbxConfigurator (VALSET costellazioni/frequenza/baud, MON-VER, ACK/NAK, ripetuto a ogni connessione)
```

## Riuso (ricerca fatta, da citare nel piano)
- Parsing NMEA: libreria NuGet NmeaParser (dotMorten), invece di scrivere il parser. Va verificato che gestisca i talker GN/GA/GB e GSV multi-costellazione.
- GPX: scrittura XML semplice con `System.Xml` (formato GPX 1.1), nessuna dipendenza.
- Mappa: Leaflet + tile OSM via un piccolo gestore locale che mette in cache i tile. Rispettare la policy OSM (User-Agent proprio, niente scaricamento massivo).
- Driver: com0com (MIT), build firmata per Windows 11.
- Riferimenti di prassi: guida com0com + Google Earth (chiefdennis.github.io/projects/nmea_ip_to_google_earth), manuale integrazione MAX-M10S per CFG-SIGNAL.

## Riavvio, offline, taglio di corrente
- La configurazione M10 «migliore» (profilo predefinito, vedi Impostazioni) viene scritta in RAM+BBR+flash (decisione utente 16/11: di default in flash). Per non consumare la flash, prima si legge con VALGET e si scrive solo se qualcosa differisce dal profilo; a ogni connessione si ricontrolla (idempotente).

## Impostazioni (approvato con aggiunta dell'utente 16/11)
Due sezioni separate nella scheda Impostazioni:
- **Programma**: porta reale (auto o fissa) e baud, coppia COM virtuale e baud per Earth (4800), frequenza verso Earth, cartella GPX, filtro punti fermi, cache tile (dimensione, svuota), avvio con Windows, lingua/tema, avvio automatico di ponte e registrazione.
- **Ricevitore**: costellazioni e segnali attivi, frequenza di aggiornamento (1-10 Hz), modello dinamico (pedone/auto/aereo/portatile), frasi NMEA in uscita, baud UART, SBAS, filtro di elevazione/soglia SNR; stato attuale letto dal ricevitore; pulsanti «Applica profilo migliore», «Leggi dal ricevitore», «Salva in flash», «Ripristina predefiniti u-blox».
- **Profilo migliore** (mia scelta, da verificare col ricevitore collegato e sul manuale dell'M10): tutte le costellazioni ammesse, 5 Hz come compromesso tra prestazioni e stabilita' (10 Hz selezionabile), modello dinamico «portatile», NMEA 4.10 con GGA/RMC/GSA/GSV/VTG/GLL, baud 115200 se stabile. Il ricevitore non e' collegato durante la scrittura del piano: i valori e le chiavi CFG-* si confermano con MON-VER/VALGET alla prima prova.
- Cavo scollegato/ricollegato: riconnessione automatica con backoff, il GPX resta aperto e riprende, la COM virtuale resta viva (Earth non si accorge).
- Chiusura anomala: il GPX e' scritto a ogni punto con chiusura dei tag; all'avvio successivo un file senza `</gpx>` viene riparato.
- Senza rete: la mappa usa i tile in cache, il resto non cambia.

## Lasciato al costruttore
Nomi delle classi, layout della UI (schede Mappa / Diagnostica / Traccia / Configurazione), icona, impostazione del limite di cache dei tile, test con un registratore NMEA di prova (file riprodotto) al posto dell'hardware per i test automatici.

## Decisioni mie da controllare
- Il GM10 puo' avere un firmware con meno costellazioni: l'app mostra il risultato reale, non promette 4.
- Nome «GpsBridge» e cartella `Documents\GpsBridge`, repo git locale, non nel repo EnjoyBuds.
- Uscita verso Earth a 1 Hz di default (Earth non serve di piu'), il GPX e la mappa usano la frequenza piena (fino a 10 Hz).

## Aggiornamento 16/11: Earth senza COM virtuale
com0com e' bloccato da HVCI/Secure Boot su questo PC (errore 52) e gli altri driver sono a pagamento. Scelta dell'utente: KML live (NetworkLink su http://127.0.0.1:8765, aggiornamento 1 s, segue la posizione). La COM virtuale resta opzionale (impostazione EarthComEnabled, spenta di default).
