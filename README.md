# MtVid

MtVid, videolari ozel bir paket formatina (`.mtaf`) cevirmek ve sadece uygulama icinden sifre ile acmak icin yazilmis bir .NET 8 aracidir.

## Ozellikler

- AES-256-GCM ile chunk bazli sifreleme
- Ozel dosya formati (`MTAF` baslik + sifreli chunklar)
- Sifre dogrulama (yanlis sifreyle paket acilmaz)
- RAM uzerinden cozumleme (diskte sifresiz kopya olusmaz)
- HTTP Range destegi ile seek/stream uyumlu oynatma (`206 Partial Content`)

## Komutlar

### 1) Paketleme

```bash
dotnet run --project MtVid -- pack \
  --input /path/video.mp4 \
  --output /path/video.mtaf \
  --password "strong-pass" \
  --chunk-mb 2
```

Opsiyonlar:

- `--chunk-mb` (varsayilan: `2`)
- `--content-type` (varsayilan: dosya uzantisindan tahmin edilir)
- `--iterations` (varsayilan: `210000`, PBKDF2-SHA256)

### 2) Acma/Stream

```bash
dotnet run --project MtVid -- serve \
  --input /path/video.mtaf \
  --password "strong-pass" \
  --port 8080
```

Uygulama basladiginda stream URL:

- `http://localhost:8080/stream`

Bu URL'yi kendi player komponentine kaynak olarak verip oynatabilirsin.

### 3) Dahili Player UI Ekrani

```bash
dotnet run --project MtVid -- serve \
  --port 8080 \
  --ui true \
  --open true
```

- `--ui true`: root adreste (`/`) ozel player ekranini acar
- `--open true`: varsayilan tarayiciyi otomatik acmayi dener

Not: UI modunda `--input` vermek zorunda degilsin. Uygulama acildiktan sonra player ekranindaki formdan
`.mtaf` dosya yolunu ve sifreyi girip dosyayi acabilirsin.

UI adresi:

- `http://localhost:8080/`

Video stream adresi:

- `http://localhost:8080/stream`

## Player Icinden Dosya Secme

1. Uygulamayi UI modunda baslat:

```bash
dotnet run --project MtVid -- serve --ui true --open true --port 8080
```

2. Acilan player ekraninda:

- `.mtaf dosya yolu` alanina dosya yolunu yaz
- `Sifre` alanina playback sifresini gir
- `Dosyayi Ac` butonuna bas

3. Dosya basariyla acildiginda video otomatik olarak `/stream` kaynagindan oynatilir.

## Player Icinden Video Sifreleme (.mtaf Olusturma)

UI ekraninda artik ikinci bir form var ve dogrudan video dosyasi sifreleyebilirsin:

1. Uygulamayi UI modunda baslat:

```bash
dotnet run --project MtVid -- serve --ui true --open true --port 8084
```
2. Player ekranindaki `Videoyu Sifrele (.mtaf)` bolumunu doldur:

- `Kaynak video dosyasi`: dosya secici ile videoyu sec
- `Cikti dosya adi (.mtaf)`: indirilecek dosya adi
- `Sifre`: paket sifresi
- `Chunk MB`: chunk boyutu (1-32)

3. `Videoyu Sifrele (.mtaf)` butonuna bas.

  - Ilk asamada yukleme ilerlemesi gorunur.
  - Sonra sunucu tarafi sifreleme ilerlemesi yuzde olarak gorunur.

4. Basarili olunca `.mtaf` dosyasi tarayici ile indirilir.

5. Alttaki acma formunda `.mtaf dosyasi` secici ile indirilen dosyayi sec, sifreyi gir ve `Dosyayi Ac` ile oynat.

## Klasor Secerek Toplu Sifreleme

UI ekraninda `Klasor Sec ve Toplu Sifrele` bolumu ile klasor bazli akis desteklenir:

1. `Toplu sifre` ve `Chunk MB` gir.
2. `Klasor Sec ve Toplu Sifrele` butonuna bas.
3. Acilan klasor seciciden hedef klasoru sec.
4. Uygulama, klasordeki `.mtaf` uzantili olmayan dosyalari sirayla isler.
5. Her dosyanin sifreli cikti hali ayni klasore `<dosyaadi>.mtaf` olarak yazilir.
6. `Orijinal dosyayi sifreleme sonrasi sil` seciliyse islenen kaynak dosya silinir.

Notlar:

- Toplu akis, tarayicinin `showDirectoryPicker` ozelligini desteklemesini gerektirir.
- Islemler siralidir (tek tek) ve progress bar toplam ilerlemeyi gosterir.

## Ornek WPF Kullanimi

WPF `MediaElement` icin kaynak:

```csharp
mediaElement.Source = new Uri("http://localhost:8080/stream");
mediaElement.Play();
```

## Paket Formati (Ozet)

Baslik alani:

- Magic: `MTAF`
- Version
- ChunkSize
- OriginalLength
- ChunkCount
- PBKDF2 Iteration
- Salt (16 byte)
- NoncePrefix (4 byte)
- PasswordVerifier (16 byte)
- ContentType

Veri alani:

Her chunk icin:

- Ciphertext (plaintext ile ayni uzunluk)
- GCM Tag (16 byte)

Nonce chunk bazinda uretilir: `NoncePrefix + chunkIndex`.

## Guvenlik Notlari

- Sifre dogrudan anahtar olarak kullanilmaz, PBKDF2 ile turetilir.
- Sifre kontrolu sabit zamanli karsilastirma ile yapilir.
- Anahtar ve hassas bufferlar kullanildiktan sonra sifirlanir.
- Sifresiz video diske yazilmaz.

## Build

```bash
dotnet build MtVid.sln
```

## Durum

Bu repo, CLI ve local stream sunucusu olarak calisir durumdadir.
Smoke test sonuclari:

- Dogru sifre ile stream: `HTTP/1.1 206 Partial Content`
- Yanlis sifre ile acma denemesi: `Wrong password. Package could not be unlocked.`
