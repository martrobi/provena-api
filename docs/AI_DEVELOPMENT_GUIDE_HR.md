# Kako razvijati Provenu uz Codex i Claude Code u VS Codeu

## Što je riješeno i što prvo provjeriti

Repozitorij sadrži AppDbContext, EF model, design-time factory, početnu migraciju i snapshot. Persistence sloj postoji; ne treba ga ponovno implementirati.

U kodu postoje tenant filtri, provjere organizacije pri spremanju i složeni strani ključevi. To nije isto što i izvršeni test. Za lokalnu povratnu informaciju bez Dockera pokreni restore, Release build i unit testove kako je opisano u [TESTING.md](TESTING.md). Puna provjera `pwsh -File scripts/verify.ps1` uključuje i integracijske testove koji zahtijevaju Docker.

Prije prve nove migracije pročitaj DATABASE.md: povijesni ciljni model početne migracije zamrznut je u `InitialCreate.TargetModel.cs`, odvojeno od snapshota koji EF mijenja pri budućim migracijama. Ne mijenjaj tu datoteku. Povijesni nalaz je u [izvještaju](AI_DISCOVERY_REPORT.md).

## Kako ovaj sustav štedi kontekst

AGENTS.md je kratki ulaz i usmjerivač. Sadrži pravila za cijeli projekt i tablicu: za ovu vrstu promjene otvori ovaj dokument i ovu proceduru. Ne sadrži cijelu arhitekturu. Za sitan popravak agent treba relevantni kod i test; za promjenu tokena treba i SECURITY; za novi odnos u bazi treba DATABASE i domenu.

Markdown nije jamstvo poslušnosti, test prolaza ili sigurnosti. Njegova vrijednost je da agent brzo pronađe relevantan kontekst. Aktualni kod ostaje dokaz što sustav radi. Odobrene odluke određuju što želimo promijeniti. Ako se razlikuju, agent mora pokazati razliku, a ne prešutno izabrati pogodniju verziju. Stari plan nikad ne smije nadjačati noviju implementaciju.

## Što se nalazi gdje

| Datoteka | Kada je koristiš |
| --- | --- |
| `AGENTS.md` | Početak svakog zadatka; pravila i usmjeravanje |
| `docs/PRODUCT.md` | Svrha, korisnici i granice proizvoda |
| `docs/CODEBASE_MAP.md` | Gdje je konkretan endpoint, servis, model ili test |
| `docs/EUDR_DOMAIN.md` | Pojmovi, stvarni statusi, evidence/reviewer granica i budući Producer/Plot |
| `docs/ARCHITECTURE.md` | Tok zahtjeva, DI, transakcije i granice komponenti |
| `docs/DATABASE.md` | Stvarni model, tenant zaštite, ograničenja i postupak migracije |
| `docs/API.md` | HTTP ugovori, validacija, CSV i ZIP format |
| `docs/SECURITY.md` | Autorizacija, organizacije, tokeni, datoteke i sigurnosne praznine |
| `docs/TESTING.md` | Stvarni testovi, preduvjeti, komande i što nedostaje |
| `docs/DEPLOYMENT.md` | Lokalno pokretanje, konfiguracija i postojeća infrastruktura |
| `docs/AI_DISCOVERY_REPORT.md` | Prioritetni nalazi ovog pregleda; nije obavezno čitanje svaki put |
| `docs/adr/` | Važne dugoročne odluke i zašto postoje |
| `docs/plans/` | Planovi većih promjena; predložak je početna točka |
| `.agents/skills/` | Kratki ponovljivi postupci za specifične vrste posla |
| `scripts/verify.ps1` | Restore, Release build i oba test projekta |

Tehnička dokumentacija je na engleskom radi dosljednog konteksta za agente; ovaj vodič je na hrvatskom. Ne treba svaki zadatak uključiti vodič.

## Postavi Codex i Claude Code u VS Codeu

Oba alata koriste isti projektni izvor pravila: `AGENTS.md`. Nemoj održavati dvije kopije arhitekture, sigurnosnih pravila ili workflowa. `.agents/skills/` ostaje jedino mjesto za Provenine repo-procedure; router iz `AGENTS.md` kaže kada ih treba otvoriti.

### Codex

1. U VS Codeu instaliraj službenu [Codex ekstenziju za VS Code](https://marketplace.visualstudio.com/items?itemName=OpenAI.chatgpt) izdavača OpenAI.
2. Otvori korijenski direktorij Provena repozitorija, otvori Codex bočnu traku i prijavi se svojim ChatGPT računom. Ako ikona nije vidljiva, u Command Paletteu pokreni `Codex: Open Codex Sidebar`.
3. Codex učitava `AGENTS.md` kao repo-upute. Kad tablica u `AGENTS.md` usmjeri zadatak na `.agents/skills/<naziv>/SKILL.md`, slijedi taj postupak. Ne stvaraj paralelnu kopiju pravila.

### Claude Code

1. U VS Code Extensions otvori `Ctrl+Shift+X`, potraži **Claude Code** i instaliraj službenu Anthropic ekstenziju. Otvori Provena root i prijavi se Claude računom; Claude Pro je dovoljan i za prijavu kroz ekstenziju nije potreban zaseban API ključ.
2. Koristi Claude Code v2.1.277 ili noviji. Te verzije mogu izravno učitati postojeći `AGENTS.md`, pa za ovaj repo **ne treba dodavati `CLAUDE.md`** samo radi dupliranja instrukcija.
3. Claude po zadanom čita `AGENTS.md` kada u radnom direktoriju ili iznad njega nema projektnog `CLAUDE.md`/`CLAUDE.local.md`. Provjeri učitavanje naredbom `/memory` ili porukom pri početku sesije koja navodi da je `AGENTS.md` učitan.
4. Claude ne auto-discovera `.agents/skills/` kao svoj nativni `.claude/skills/` direktorij. U Proveni je to namjerno: `AGENTS.md` eksplicitno usmjerava na odgovarajući `.agents/skills/.../SKILL.md`, koji Claude može pročitati kao običnu repo-proceduru. Nemoj kopirati iste skillove u `.claude/skills/` jer bi nastala dva izvora istine.
5. Ako ikad moraš podržati stariju Claude Code sesiju koja ne čita `AGENTS.md`, tada dodaj minimalni `CLAUDE.md` s retkom `@AGENTS.md`; ne kopiraj sadržaj `AGENTS.md`.

Za oba alata provjeri kontekst kratkim zadatkom: „Pročitaj projektne upute. Za promjenu validacije CN koda navedi koje relevantne dokumente, skillove, datoteke i testove trebaš otvoriti. Ne mijenjaj kod.” Očekuj API, validator i relevantne testove te domenu samo ako je potrebna. Potom pregledaj diff i potvrdi da se agent drži opsega.

OpenAI upute za [Codex IDE ekstenziju](https://developers.openai.com/codex/ide) i [AGENTS.md](https://developers.openai.com/cookbook/examples/gpt-5/codex_prompting_guide) opisuju Codex setup. Anthropicove upute za [Claude Code u VS Codeu](https://code.claude.com/docs/en/ide-integrations) i [AGENTS.md / project instructions](https://code.claude.com/docs/en/memory) opisuju Claude setup. Provjereno 2026-10-12.

## Skills, ADR i plan nisu ista stvar

Skill je recept za ponovljivu aktivnost: primjerice promijeni model → mapping → generiraj i pregledaj migraciju → testiraj → prijavi rezultat. Ovdje ih je pet: database-change, api-contract-change, eudr-domain-change, security-sensitive-change i verify-change. Ne spremaju stanje projekta i ne zamjenjuju čitanje konkretnog koda.

ADR objašnjava važnu odluku koja treba preživjeti više featurea. ADR 0001 opisuje zatečeni servisni ustroj, bez izmišljene povijesti odobravanja. ADR 0002 bilježi tvoju odluku da Supplier, Producer i ProductionPlot nisu isti pojam, uz jasnu oznaku da cilj još nije implementiran.

Plan je razrada konkretne veće promjene: problem, željeno ponašanje, nepoznanice, model/API, sigurnost, migracija, kriteriji prihvaćanja i testovi. Kreiraj ga kada mijenjaš više povezanih dijelova, poslovna pravila, povijesne podatke, javni ugovor ili sigurnosnu granicu. Za tipfeler, lokalni refaktor bez promjene ponašanja ili jasan mali validator ne treba plan od pet stranica. Razina rizika važnija je od broja redaka.

## Primjer: sitna promjena

Nakon što provjeriš osnovni build:

```text
Pročitaj AGENTS.md i samo relevantni kontekst. U SupplierValidator smanji
maksimalnu duljinu ContactPerson s 200 na 150 znakova. To je moja odobrena
promjena pravila. Provjeri utjecaj na API i postojeće podatke; nemoj mijenjati
shemu ako nije potrebno. Dodaj smislen granični test za 150/151 znak.
Bez drugih refaktora. Na kraju navedi promjenu i stvarno izvršene provjere.
```

Jasno si odredio pravilo. AI ne treba izmišljati razlog promjene ni uvoditi novi validacijski framework. Ako model ima drugo ograničenje, treba pokazati nesklad.

## Primjer: srednja funkcionalnost

```text
Pročitaj AGENTS.md. Želim dodati opcionalni filter isActive na GET /api/products.
Kad parametar nije poslan, rezultat mora ostati kao danas; true/false filtrira
aktivne/neaktivne proizvode iste organizacije. Zadrži postojeći search i oblik
odgovora. Prvo ukratko navedi zahvaćene datoteke i testove, zatim implementiraj
ako nema nejasnoće. Koristi api-contract-change; provjeri izolaciju organizacija
i ažuriraj samo relevantni dio API.md. Bez paginacije i novih slojeva.
```

Ovo je dovoljno precizan zahtjev da agent može završiti posao bez višekratnog traženja dozvole za svaku datoteku. Ako se pojavi stvarna odluka, treba postaviti usko pitanje i nastaviti neupitni dio posla.

## Primjer: velika domenska promjena

```text
Pročitaj AGENTS.md, EUDR_DOMAIN.md, DATABASE.md i ADR 0002, pa samo povezani kod.
Želim dodati Producer i ProductionPlot. U ovom zadatku napravi plan prema
predlošku, bez implementacije. Najprije prikaži postojeći model i otvorene
odluke: odnos Supplier–Producer, vlasništvo plotova, identitet, povezivanje sa
shipmentom i migracija postojeće geolokacije. Ne pretpostavljaj da je svaki
Supplier Producer ili svaka GeoJSON feature zaseban plot. Predloži opcije i
posljedice, kriterije prihvaćanja, tenant testove i rollout. Poslovne odluke
potvrđujem ja prije koda.
```

Kad odobriš konkretan plan i odluke, novi zadatak može biti „Implementiraj odobreni plan X, korak 1–3; ne mijenjaj preostale odluke.” Ne treba ponovno analizirati cijeli backend ako je plan i dalje aktualan.

## Discovery bez trošenja cijelog konteksta

```text
Pročitaj AGENTS.md. Istraži samo regeneraciju supplier tokena i njene pozivatelje.
Ne mijenjaj kod. Navedi tok, datoteke, postojeće testove i praznine. Posebno
provjeri zadržava li se evidence i invalidira li se stari token nakon čekanja
na case lock. Širi pretragu samo ako pronađeš stvarnu ovisnost.
```

Traži odgovore vezane uz metode/testove, ne esej o općem .NET-u. Cijeli repo discovery ponavljaš tek kod ozbiljnog nesklada dokumentacije ili velike promjene arhitekture.

## Kako tražiti code review

```text
Pregledaj diff ovog brancha prema stvarnom baznom branchu. Prvo pročitaj
AGENTS.md i relevantne upute. Nemoj popravljati kod. Traži regresije,
zaobilaženje tenant/autorizacijskih granica, pogrešne status prijelaze,
konkurentne zahtjeve, gubitak podataka i API breaking changes. Svaki nalaz
potkrijepi datotekom/metodom i scenarijem. Odvoji potvrđene probleme od sumnji.
Navedi što testovi ne pokrivaju i koje provjere nisi mogao izvršiti.
```

Za review odaberi stvarni base branch, nemoj automatski pretpostaviti main ili develop. Još jedan AI review je koristan, ali nije dokaz ispravnosti.

## Kako riješiti nejasno pravilo

Primjer: „Smije li Member označiti requirement NotRequired bez obrazloženja?” Danas endpoint nema zasebnu role provjeru i bilješka je opcionalna. To je činjenica, ne odgovor na pitanje što proizvod treba dopuštati. AI treba prikazati postojeće ponašanje, opcije i posljedice, a ti odlučuješ. Odluku zatim pretvori u kriterij i test. Za dugoročnu važnu promjenu koristi ADR; za lokalno pravilo dovoljan je odobren zahtjev i domenska dokumentacija.

Nemoj napisati „napravi da bude EUDR compliant” i očekivati da agent sam odluči pravne obveze. Definiraj konkretan zahtjev i pouzdan izvor koji je ovlaštena osoba protumačila. Geometrijski valjan poligon nije regulatorni zaključak.

## Git i PR radni tok

1. Počni iz čistog, ažuriranog checkouta stvarnog baznog brancha. Sačuvaj svoje postojeće izmjene; agent ih ne smije pregaziti.
2. Otvori branch za jedan cilj, npr. `docs/ai-foundation` ili `feat/product-active-filter`.
3. Zadaj opseg, kriterije i granice. Za veće promjene prvo dogovori plan; za male pusti agenta da završi.
4. Pregledaj diff prije commita: neočekivane datoteke, pakete, masovno formatiranje, tajne, migracije i promjene testova.
5. Bez Dockera možeš pokrenuti restore, Release build i unit testove; to je lokalna provjera, ne puna provjera. `pwsh -File scripts/verify.ps1` radi restore, Release build i unit/integration testove; Docker je potreban za integracijske. Preskočeno ili blokirano nije prošlo.
6. PR neka objasni problem, promjenu, rezultate provjera i preostale rizike. Ti pregledaj poslovno pravilo i sigurnosne granice prije mergea. Nije dodan CI workflow niti branch protection; to treba posebno postaviti.

Prije prihvaćanja AI promjene pitaj: mijenja li više nego što sam tražio; štiti li organizaciju i public token; čuva li review nakon promjene evidencea na dogovoreni način; ima li smislen negativni test; što radi s postojećim podacima; je li test stvarno pokrenut; jesu li promijenjeni ugovor i dokumentacija usklađeni?

## Održavanje bez dokumentacijskog tereta

Jedna činjenica treba imati jedno glavno mjesto. API ugovor pripada API.md, redoslijed statusa EUDR_DOMAIN.md, konfiguracija DEPLOYMENT.md. Ostali dokumenti upućuju na njih. Kod selidbe datoteke ažuriraj mapu. Kod novog sigurnosnog pristupa ažuriraj SECURITY i relevantnu proceduru. Kod promjene komande ažuriraj TESTING i skriptu zajedno.

Ne traži promjene dokumentacije za svako preimenovanje lokalne varijable. Kad plan bude proveden, upiši stvarni rezultat i commit/PR; kasniji agent treba aktualni kod i dokumentaciju, ne sve stare planove. Povijesni discovery izvještaj čuvaj, a riješene nalaze označi dokazom i ukloni zastarjele aktivne blokade iz routera.

## Najčešće pogreške

- Učitavanje svih MD datoteka u svaki prompt. To troši kontekst i otežava pronalaženje relevantnog pravila.
- Ogroman AGENTS.md s kopijom arhitekture. Router treba ostati kratak.
- „Uljepšaj backend” bez cilja. To često donosi nepotrebne slojeve i velik diff.
- Prihvaćanje prijedloga kao odobrene poslovne odluke ili budućeg entiteta kao postojećeg.
- Vjerovanje da naziv testa znači da je test prošao ili da su pokriveni svi tenant scenariji.
- Popravljanje greške uklanjanjem testa, warning-as-error ili sigurnosne provjere.
- Traženje velikog featurea preko nestabilnog builda. Prvo potvrdi bazni build i testove.

Prvi korak može biti lokalna provjera bez Dockera kako je navedeno u TESTING.md; prije mergea pokreni punu provjeru s integracijskim testovima. Ako nešto padne, podijeli točan izlaz s agentom koji radi na zadatku i zatraži usku analizu uzroka. Prije schema featurea riješi povezanost početne migracije i snapshota. Za ostali razvoj koristi jasno zadane promjene s ograničenim discoveryjem.
