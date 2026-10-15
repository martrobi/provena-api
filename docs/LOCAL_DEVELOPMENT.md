# Lokalni razvoj i provjera kompatibilnosti

CI/CD, deploy i hosting odgođeni su odlukom vlasnika. Ne dodavati njihove konfiguracije usput. Lokalna provjera ostaje obvezna prema opsegu promjene.

1. Za brzu backend provjeru: restore, Release build i unit testovi prema [TESTING](TESTING.md). Ne zahtijeva Docker.
2. Za punu backend provjeru pokrenuti `scripts/verify.ps1` ili `scripts/verify.sh`; PostgreSQL Testcontainers zahtijeva Docker.
3. Za browser provjeru koristiti zasebnu lokalnu bazu i direktorij dokaza, bez stvarnih podataka. Pokrenuti API prema [DEPLOYMENT](DEPLOYMENT.md), a client prema njegovu `docs/LOCAL_TESTING.md`. Client sadrži izvršive browser testove i zaseban live suite protiv stvarnog API-ja.
4. U [handoff](CHANGE_WORKFLOW.md) zabilježiti backend i frontend commit, dirty stanje, komandu i rezultat. Test s mock odgovorima nije dokaz kompatibilnosti sa stvarnim API-jem.

## Redoslijed daljnjeg rada

- Održavati upute usklađene s kodom i pokrenuti postojeće lokalne provjere.
- Provjeriti ključni workflow u browseru protiv lokalnog API-ja.
- Dogovoriti preostale poslovne odluke: izuzeća (waiver), količine i ispravci nakon zatvaranja, tko smije arhivirati osim Ownera. Zatvaranje predmeta je odlučeno u [ADR 0009](adr/0009-case-lifecycle-v2.md): Owner zatvara samo na temelju aktualnog (Current) Owner pregleda, Owner ponovno otvara i arhivira uz razlog. Ovaj zadatak ne mijenja ta pravila.
- Poboljšavati korisnički tok i smanjivati ručni unos prema stvarnom lokalnom testiranju.
- Tek kada vlasnik odluči pokrenuti vanjski pilot, ponovno razmotriti dostavu e-maila, hosting, operativne sigurnosne mjere i CI/CD. Dokumentirani budući rizici ne znače da te sustave treba graditi sada.
