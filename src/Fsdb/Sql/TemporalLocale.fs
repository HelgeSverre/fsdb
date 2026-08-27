module internal Fsdb.TemporalLocale

type Names =
    { Months: string array
      AbbreviatedMonths: string array
      Days: string array
      AbbreviatedDays: string array }

let private arAe =
    { Months =
        [| "يناير"
           "فبراير"
           "مارس"
           "أبريل"
           "مايو"
           "يونيو"
           "يوليو"
           "أغسطس"
           "سبتمبر"
           "أكتوبر"
           "نوفمبر"
           "ديسمبر" |]
      AbbreviatedMonths =
        [| "ينا"
           "فبر"
           "مار"
           "أبر"
           "ماي"
           "يون"
           "يول"
           "أغس"
           "سبت"
           "أكت"
           "نوف"
           "ديس" |]
      Days = [| "الاثنين"; "الثلاثاء"; "الأربعاء"; "الخميس"; "الجمعة"; "السبت "; "الأحد" |]
      AbbreviatedDays = [| "ن"; "ث"; "ر"; "خ"; "ج"; "س"; "ح" |] }

let private arBh =
    { Months =
        [| "يناير"
           "فبراير"
           "مارس"
           "أبريل"
           "مايو"
           "يونيو"
           "يوليو"
           "أغسطس"
           "سبتمبر"
           "أكتوبر"
           "نوفمبر"
           "ديسمبر" |]
      AbbreviatedMonths =
        [| "ينا"
           "فبر"
           "مار"
           "أبر"
           "ماي"
           "يون"
           "يول"
           "أغس"
           "سبت"
           "أكت"
           "نوف"
           "ديس" |]
      Days = [| "الاثنين"; "الثلاثاء"; "الأربعاء"; "الخميس"; "الجمعة"; "السبت"; "الأحد" |]
      AbbreviatedDays = [| "ن"; "ث"; "ر"; "خ"; "ج"; "س"; "ح" |] }

let private arJo =
    { Months =
        [| "كانون الثاني"
           "شباط"
           "آذار"
           "نيسان"
           "نوار"
           "حزيران"
           "تموز"
           "آب"
           "أيلول"
           "تشرين الأول"
           "تشرين الثاني"
           "كانون الأول" |]
      AbbreviatedMonths =
        [| "كانون الثاني"
           "شباط"
           "آذار"
           "نيسان"
           "نوار"
           "حزيران"
           "تموز"
           "آب"
           "أيلول"
           "تشرين الأول"
           "تشرين الثاني"
           "كانون الأول" |]
      Days = [| "الاثنين"; "الثلاثاء"; "الأربعاء"; "الخميس"; "الجمعة"; "السبت"; "الأحد" |]
      AbbreviatedDays = [| "الاثنين"; "الثلاثاء"; "الأربعاء"; "الخميس"; "الجمعة"; "السبت"; "الأحد" |] }

let private arSa =
    { Months =
        [| "كانون الثاني"
           "شباط"
           "آذار"
           "نيسـان"
           "أيار"
           "حزيران"
           "تـمـوز"
           "آب"
           "أيلول"
           "تشرين الأول"
           "تشرين الثاني"
           "كانون الأول" |]
      AbbreviatedMonths =
        [| "Jan"
           "Feb"
           "Mar"
           "Apr"
           "May"
           "Jun"
           "Jul"
           "Aug"
           "Sep"
           "Oct"
           "Nov"
           "Dec" |]
      Days = [| "الإثنين"; "الثلاثاء"; "الأربعاء"; "الخميس"; "الجمعـة"; "السبت"; "الأحد" |]
      AbbreviatedDays = [| "Mon"; "Tue"; "Wed"; "Thu"; "Fri"; "Sat"; "Sun" |] }

let private arSy =
    { Months =
        [| "كانون الثاني"
           "شباط"
           "آذار"
           "نيسان"
           "نواران"
           "حزير"
           "تموز"
           "آب"
           "أيلول"
           "تشرين الأول"
           "تشرين الثاني"
           "كانون الأول" |]
      AbbreviatedMonths =
        [| "كانون الثاني"
           "شباط"
           "آذار"
           "نيسان"
           "نوار"
           "حزيران"
           "تموز"
           "آب"
           "أيلول"
           "تشرين الأول"
           "تشرين الثاني"
           "كانون الأول" |]
      Days = [| "الاثنين"; "الثلاثاء"; "الأربعاء"; "الخميس"; "الجمعة"; "السبت"; "الأحد" |]
      AbbreviatedDays = [| "الاثنين"; "الثلاثاء"; "الأربعاء"; "الخميس"; "الجمعة"; "السبت"; "الأحد" |] }

let private beBy =
    { Months =
        [| "Студзень"
           "Люты"
           "Сакавік"
           "Красавік"
           "Травень"
           "Чэрвень"
           "Ліпень"
           "Жнівень"
           "Верасень"
           "Кастрычнік"
           "Лістапад"
           "Снежань" |]
      AbbreviatedMonths =
        [| "Стд"
           "Лют"
           "Сак"
           "Крс"
           "Тра"
           "Чэр"
           "Ліп"
           "Жнв"
           "Врс"
           "Кст"
           "Ліс"
           "Снж" |]
      Days =
        [| "Панядзелак"
           "Аўторак"
           "Серада"
           "Чацвер"
           "Пятніца"
           "Субота"
           "Нядзеля" |]
      AbbreviatedDays = [| "Пан"; "Аўт"; "Срд"; "Чцв"; "Пят"; "Суб"; "Няд" |] }

let private bgBg =
    { Months =
        [| "януари"
           "февруари"
           "март"
           "април"
           "май"
           "юни"
           "юли"
           "август"
           "септември"
           "октомври"
           "ноември"
           "декември" |]
      AbbreviatedMonths =
        [| "яну"
           "фев"
           "мар"
           "апр"
           "май"
           "юни"
           "юли"
           "авг"
           "сеп"
           "окт"
           "ное"
           "дек" |]
      Days = [| "понеделник"; "вторник"; "сряда"; "четвъртък"; "петък"; "събота"; "неделя" |]
      AbbreviatedDays = [| "пн"; "вт"; "ср"; "чт"; "пт"; "сб"; "нд" |] }

let private caEs =
    { Months =
        [| "gener"
           "febrer"
           "març"
           "abril"
           "maig"
           "juny"
           "juliol"
           "agost"
           "setembre"
           "octubre"
           "novembre"
           "desembre" |]
      AbbreviatedMonths =
        [| "gen"
           "feb"
           "mar"
           "abr"
           "mai"
           "jun"
           "jul"
           "ago"
           "set"
           "oct"
           "nov"
           "des" |]
      Days =
        [| "dilluns"
           "dimarts"
           "dimecres"
           "dijous"
           "divendres"
           "dissabte"
           "diumenge" |]
      AbbreviatedDays = [| "dl"; "dt"; "dc"; "dj"; "dv"; "ds"; "dg" |] }

let private csCz =
    { Months =
        [| "leden"
           "únor"
           "březen"
           "duben"
           "květen"
           "červen"
           "červenec"
           "srpen"
           "září"
           "říjen"
           "listopad"
           "prosinec" |]
      AbbreviatedMonths =
        [| "led"
           "úno"
           "bře"
           "dub"
           "kvě"
           "čen"
           "čec"
           "srp"
           "zář"
           "říj"
           "lis"
           "pro" |]
      Days = [| "Pondělí"; "Úterý"; "Středa"; "Čtvrtek"; "Pátek"; "Sobota"; "Neděle" |]
      AbbreviatedDays = [| "Po"; "Út"; "St"; "Čt"; "Pá"; "So"; "Ne" |] }

let private daDk =
    { Months =
        [| "januar"
           "februar"
           "marts"
           "april"
           "maj"
           "juni"
           "juli"
           "august"
           "september"
           "oktober"
           "november"
           "december" |]
      AbbreviatedMonths =
        [| "jan"
           "feb"
           "mar"
           "apr"
           "maj"
           "jun"
           "jul"
           "aug"
           "sep"
           "okt"
           "nov"
           "dec" |]
      Days = [| "mandag"; "tirsdag"; "onsdag"; "torsdag"; "fredag"; "lørdag"; "søndag" |]
      AbbreviatedDays = [| "man"; "tir"; "ons"; "tor"; "fre"; "lør"; "søn" |] }

let private deAt =
    { Months =
        [| "Jänner"
           "Feber"
           "März"
           "April"
           "Mai"
           "Juni"
           "Juli"
           "August"
           "September"
           "Oktober"
           "November"
           "Dezember" |]
      AbbreviatedMonths =
        [| "Jän"
           "Feb"
           "Mär"
           "Apr"
           "Mai"
           "Jun"
           "Jul"
           "Aug"
           "Sep"
           "Okt"
           "Nov"
           "Dez" |]
      Days =
        [| "Montag"
           "Dienstag"
           "Mittwoch"
           "Donnerstag"
           "Freitag"
           "Samstag"
           "Sonntag" |]
      AbbreviatedDays = [| "Mon"; "Die"; "Mit"; "Don"; "Fre"; "Sam"; "Son" |] }

let private deBe =
    { Months =
        [| "Januar"
           "Februar"
           "März"
           "April"
           "Mai"
           "Juni"
           "Juli"
           "August"
           "September"
           "Oktober"
           "November"
           "Dezember" |]
      AbbreviatedMonths =
        [| "Jan"
           "Feb"
           "Mär"
           "Apr"
           "Mai"
           "Jun"
           "Jul"
           "Aug"
           "Sep"
           "Okt"
           "Nov"
           "Dez" |]
      Days =
        [| "Montag"
           "Dienstag"
           "Mittwoch"
           "Donnerstag"
           "Freitag"
           "Samstag"
           "Sonntag" |]
      AbbreviatedDays = [| "Mo"; "Di"; "Mi"; "Do"; "Fr"; "Sa"; "So" |] }

let private elGr =
    { Months =
        [| "Ιανουάριος"
           "Φεβρουάριος"
           "Μάρτιος"
           "Απρίλιος"
           "Μάιος"
           "Ιούνιος"
           "Ιούλιος"
           "Αύγουστος"
           "Σεπτέμβριος"
           "Οκτώβριος"
           "Νοέμβριος"
           "Δεκέμβριος" |]
      AbbreviatedMonths =
        [| "Ιαν"
           "Φεβ"
           "Μάρ"
           "Απρ"
           "Μάι"
           "Ιούν"
           "Ιούλ"
           "Αύγ"
           "Σεπ"
           "Οκτ"
           "Νοέ"
           "Δεκ" |]
      Days = [| "Δευτέρα"; "Τρίτη"; "Τετάρτη"; "Πέμπτη"; "Παρασκευή"; "Σάββατο"; "Κυριακή" |]
      AbbreviatedDays = [| "Δευ"; "Τρί"; "Τετ"; "Πέμ"; "Παρ"; "Σάβ"; "Κυρ" |] }

let private enAu =
    { Months =
        [| "January"
           "February"
           "March"
           "April"
           "May"
           "June"
           "July"
           "August"
           "September"
           "October"
           "November"
           "December" |]
      AbbreviatedMonths =
        [| "Jan"
           "Feb"
           "Mar"
           "Apr"
           "May"
           "Jun"
           "Jul"
           "Aug"
           "Sep"
           "Oct"
           "Nov"
           "Dec" |]
      Days =
        [| "Monday"
           "Tuesday"
           "Wednesday"
           "Thursday"
           "Friday"
           "Saturday"
           "Sunday" |]
      AbbreviatedDays = [| "Mon"; "Tue"; "Wed"; "Thu"; "Fri"; "Sat"; "Sun" |] }

let private esAr =
    { Months =
        [| "enero"
           "febrero"
           "marzo"
           "abril"
           "mayo"
           "junio"
           "julio"
           "agosto"
           "septiembre"
           "octubre"
           "noviembre"
           "diciembre" |]
      AbbreviatedMonths =
        [| "ene"
           "feb"
           "mar"
           "abr"
           "may"
           "jun"
           "jul"
           "ago"
           "sep"
           "oct"
           "nov"
           "dic" |]
      Days = [| "lunes"; "martes"; "miércoles"; "jueves"; "viernes"; "sábado"; "domingo" |]
      AbbreviatedDays = [| "lun"; "mar"; "mié"; "jue"; "vie"; "sáb"; "dom" |] }

let private etEe =
    { Months =
        [| "jaanuar"
           "veebruar"
           "märts"
           "aprill"
           "mai"
           "juuni"
           "juuli"
           "august"
           "september"
           "oktoober"
           "november"
           "detsember" |]
      AbbreviatedMonths =
        [| "jaan "
           "veebr"
           "märts"
           "apr  "
           "mai  "
           "juuni"
           "juuli"
           "aug  "
           "sept "
           "okt  "
           "nov  "
           "dets " |]
      Days =
        [| "esmaspäev"
           "teisipäev"
           "kolmapäev"
           "neljapäev"
           "reede"
           "laupäev"
           "pühapäev" |]
      AbbreviatedDays = [| "E"; "T"; "K"; "N"; "R"; "L"; "P" |] }

let private euEs =
    { Months =
        [| "urtarrila"
           "otsaila"
           "martxoa"
           "apirila"
           "maiatza"
           "ekaina"
           "uztaila"
           "abuztua"
           "iraila"
           "urria"
           "azaroa"
           "abendua" |]
      AbbreviatedMonths =
        [| "urt"
           "ots"
           "mar"
           "api"
           "mai"
           "eka"
           "uzt"
           "abu"
           "ira"
           "urr"
           "aza"
           "abe" |]
      Days =
        [| "astelehena"
           "asteartea"
           "asteazkena"
           "osteguna"
           "ostirala"
           "larunbata"
           "igandea" |]
      AbbreviatedDays = [| "al."; "ar."; "az."; "og."; "or."; "lr."; "ig." |] }

let private fiFi =
    { Months =
        [| "tammikuu"
           "helmikuu"
           "maaliskuu"
           "huhtikuu"
           "toukokuu"
           "kesäkuu"
           "heinäkuu"
           "elokuu"
           "syyskuu"
           "lokakuu"
           "marraskuu"
           "joulukuu" |]
      AbbreviatedMonths =
        [| "tammi "
           "helmi "
           "maalis"
           "huhti "
           "touko "
           "kesä  "
           "heinä "
           "elo   "
           "syys  "
           "loka  "
           "marras"
           "joulu " |]
      Days =
        [| "maanantai"
           "tiistai"
           "keskiviikko"
           "torstai"
           "perjantai"
           "lauantai"
           "sunnuntai" |]
      AbbreviatedDays = [| "ma"; "ti"; "ke"; "to"; "pe"; "la"; "su" |] }

let private foFo =
    { Months =
        [| "januar"
           "februar"
           "mars"
           "apríl"
           "mai"
           "juni"
           "juli"
           "august"
           "september"
           "oktober"
           "november"
           "desember" |]
      AbbreviatedMonths =
        [| "jan"
           "feb"
           "mar"
           "apr"
           "mai"
           "jun"
           "jul"
           "aug"
           "sep"
           "okt"
           "nov"
           "des" |]
      Days =
        [| "mánadagur"
           "týsdagur"
           "mikudagur"
           "hósdagur"
           "fríggjadagur"
           "leygardagur"
           "sunnudagur" |]
      AbbreviatedDays = [| "mán"; "týs"; "mik"; "hós"; "frí"; "ley"; "sun" |] }

let private frBe =
    { Months =
        [| "janvier"
           "février"
           "mars"
           "avril"
           "mai"
           "juin"
           "juillet"
           "août"
           "septembre"
           "octobre"
           "novembre"
           "décembre" |]
      AbbreviatedMonths =
        [| "jan"
           "fév"
           "mar"
           "avr"
           "mai"
           "jun"
           "jui"
           "aoû"
           "sep"
           "oct"
           "nov"
           "déc" |]
      Days = [| "lundi"; "mardi"; "mercredi"; "jeudi"; "vendredi"; "samedi"; "dimanche" |]
      AbbreviatedDays = [| "lun"; "mar"; "mer"; "jeu"; "ven"; "sam"; "dim" |] }

let private glEs =
    { Months =
        [| "Xaneiro"
           "Febreiro"
           "Marzo"
           "Abril"
           "Maio"
           "Xuño"
           "Xullo"
           "Agosto"
           "Setembro"
           "Outubro"
           "Novembro"
           "Decembro" |]
      AbbreviatedMonths =
        [| "Xan"
           "Feb"
           "Mar"
           "Abr"
           "Mai"
           "Xuñ"
           "Xul"
           "Ago"
           "Set"
           "Out"
           "Nov"
           "Dec" |]
      Days = [| "Luns"; "Martes"; "Mércores"; "Xoves"; "Venres"; "Sábado"; "Domingo" |]
      AbbreviatedDays = [| "Lun"; "Mar"; "Mér"; "Xov"; "Ven"; "Sáb"; "Dom" |] }

let private guIn =
    { Months =
        [| "જાન્યુઆરી"
           "ફેબ્રુઆરી"
           "માર્ચ"
           "એપ્રિલ"
           "મે"
           "જુન"
           "જુલાઇ"
           "ઓગસ્ટ"
           "સેપ્ટેમ્બર"
           "ઓક્ટોબર"
           "નવેમ્બર"
           "ડિસેમ્બર" |]
      AbbreviatedMonths =
        [| "જાન"
           "ફેબ"
           "માર"
           "એપ્ર"
           "મે"
           "જુન"
           "જુલ"
           "ઓગ"
           "સેપ્ટ"
           "ઓક્ટ"
           "નોવ"
           "ડિસ" |]
      Days = [| "સોમવાર"; "મન્ગળવાર"; "બુધવાર"; "ગુરુવાર"; "શુક્રવાર"; "શનિવાર"; "રવિવાર" |]
      AbbreviatedDays = [| "સોમ"; "મન્ગળ"; "બુધ"; "ગુરુ"; "શુક્ર"; "શનિ"; "રવિ" |] }

let private heIl =
    { Months =
        [| "ינואר"
           "פברואר"
           "מרץ"
           "אפריל"
           "מאי"
           "יוני"
           "יולי"
           "אוגוסט"
           "ספטמבר"
           "אוקטובר"
           "נובמבר"
           "דצמבר" |]
      AbbreviatedMonths =
        [| "ינו"
           "פבר"
           "מרץ"
           "אפר"
           "מאי"
           "יונ"
           "יול"
           "אוג"
           "ספט"
           "אוק"
           "נוב"
           "דצמ" |]
      Days = [| "שני"; "שלישי"; "רביעי"; "חמישי"; "שישי"; "שבת"; "ראשון" |]
      AbbreviatedDays = [| "ב'"; "ג'"; "ד'"; "ה'"; "ו'"; "ש'"; "א'" |] }

let private hiIn =
    { Months =
        [| "जनवरी"
           "फ़रवरी"
           "मार्च"
           "अप्रेल"
           "मई"
           "जून"
           "जुलाई"
           "अगस्त"
           "सितम्बर"
           "अक्टूबर"
           "नवम्बर"
           "दिसम्बर" |]
      AbbreviatedMonths =
        [| "जनवरी"
           "फ़रवरी"
           "मार्च"
           "अप्रेल"
           "मई"
           "जून"
           "जुलाई"
           "अगस्त"
           "सितम्बर"
           "अक्टूबर"
           "नवम्बर"
           "दिसम्बर" |]
      Days =
        [| "सोमवार "
           "मंगलवार "
           "बुधवार "
           "गुरुवार "
           "शुक्रवार "
           "शनिवार "
           "रविवार " |]
      AbbreviatedDays = [| "सोम "; "मंगल "; "बुध "; "गुरु "; "शुक्र "; "शनि "; "रवि " |] }

let private hrHr =
    { Months =
        [| "Siječanj"
           "Veljača"
           "Ožujak"
           "Travanj"
           "Svibanj"
           "Lipanj"
           "Srpanj"
           "Kolovoz"
           "Rujan"
           "Listopad"
           "Studeni"
           "Prosinac" |]
      AbbreviatedMonths =
        [| "Sij"
           "Vel"
           "Ožu"
           "Tra"
           "Svi"
           "Lip"
           "Srp"
           "Kol"
           "Ruj"
           "Lis"
           "Stu"
           "Pro" |]
      Days =
        [| "Ponedjeljak"
           "Utorak"
           "Srijeda"
           "Četvrtak"
           "Petak"
           "Subota"
           "Nedjelja" |]
      AbbreviatedDays = [| "Pon"; "Uto"; "Sri"; "Čet"; "Pet"; "Sub"; "Ned" |] }

let private huHu =
    { Months =
        [| "január"
           "február"
           "március"
           "április"
           "május"
           "június"
           "július"
           "augusztus"
           "szeptember"
           "október"
           "november"
           "december" |]
      AbbreviatedMonths =
        [| "jan"
           "feb"
           "már"
           "ápr"
           "máj"
           "jún"
           "júl"
           "aug"
           "sze"
           "okt"
           "nov"
           "dec" |]
      Days = [| "hétfő"; "kedd"; "szerda"; "csütörtök"; "péntek"; "szombat"; "vasárnap" |]
      AbbreviatedDays = [| "h"; "k"; "sze"; "cs"; "p"; "szo"; "v" |] }

let private idId =
    { Months =
        [| "Januari"
           "Pebruari"
           "Maret"
           "April"
           "Mei"
           "Juni"
           "Juli"
           "Agustus"
           "September"
           "Oktober"
           "November"
           "Desember" |]
      AbbreviatedMonths =
        [| "Jan"
           "Peb"
           "Mar"
           "Apr"
           "Mei"
           "Jun"
           "Jul"
           "Agu"
           "Sep"
           "Okt"
           "Nov"
           "Des" |]
      Days = [| "Senin"; "Selasa"; "Rabu"; "Kamis"; "Jumat"; "Sabtu"; "Minggu" |]
      AbbreviatedDays = [| "Sen"; "Sel"; "Rab"; "Kam"; "Jum"; "Sab"; "Min" |] }

let private isIs =
    { Months =
        [| "janúar"
           "febrúar"
           "mars"
           "apríl"
           "maí"
           "júní"
           "júlí"
           "ágúst"
           "september"
           "október"
           "nóvember"
           "desember" |]
      AbbreviatedMonths =
        [| "jan"
           "feb"
           "mar"
           "apr"
           "maí"
           "jún"
           "júl"
           "ágú"
           "sep"
           "okt"
           "nóv"
           "des" |]
      Days =
        [| "mánudagur"
           "þriðjudagur"
           "miðvikudagur"
           "fimmtudagur"
           "föstudagur"
           "laugardagur"
           "sunnudagur" |]
      AbbreviatedDays = [| "mán"; "þri"; "mið"; "fim"; "fös"; "lau"; "sun" |] }

let private itCh =
    { Months =
        [| "gennaio"
           "febbraio"
           "marzo"
           "aprile"
           "maggio"
           "giugno"
           "luglio"
           "agosto"
           "settembre"
           "ottobre"
           "novembre"
           "dicembre" |]
      AbbreviatedMonths =
        [| "gen"
           "feb"
           "mar"
           "apr"
           "mag"
           "giu"
           "lug"
           "ago"
           "set"
           "ott"
           "nov"
           "dic" |]
      Days =
        [| "lunedì"
           "martedì"
           "mercoledì"
           "giovedì"
           "venerdì"
           "sabato"
           "domenica" |]
      AbbreviatedDays = [| "lun"; "mar"; "mer"; "gio"; "ven"; "sab"; "dom" |] }

let private jaJp =
    { Months = [| "1月"; "2月"; "3月"; "4月"; "5月"; "6月"; "7月"; "8月"; "9月"; "10月"; "11月"; "12月" |]
      AbbreviatedMonths =
        [| " 1月"
           " 2月"
           " 3月"
           " 4月"
           " 5月"
           " 6月"
           " 7月"
           " 8月"
           " 9月"
           "10月"
           "11月"
           "12月" |]
      Days = [| "月曜日"; "火曜日"; "水曜日"; "木曜日"; "金曜日"; "土曜日"; "日曜日" |]
      AbbreviatedDays = [| "月"; "火"; "水"; "木"; "金"; "土"; "日" |] }

let private koKr =
    { Months = [| "일월"; "이월"; "삼월"; "사월"; "오월"; "유월"; "칠월"; "팔월"; "구월"; "시월"; "십일월"; "십이월" |]
      AbbreviatedMonths =
        [| " 1월"
           " 2월"
           " 3월"
           " 4월"
           " 5월"
           " 6월"
           " 7월"
           " 8월"
           " 9월"
           "10월"
           "11월"
           "12월" |]
      Days = [| "월요일"; "화요일"; "수요일"; "목요일"; "금요일"; "토요일"; "일요일" |]
      AbbreviatedDays = [| "월"; "화"; "수"; "목"; "금"; "토"; "일" |] }

let private ltLt =
    { Months =
        [| "sausio"
           "vasario"
           "kovo"
           "balandžio"
           "gegužės"
           "birželio"
           "liepos"
           "rugpjūčio"
           "rugsėjo"
           "spalio"
           "lapkričio"
           "gruodžio" |]
      AbbreviatedMonths =
        [| "Sau"
           "Vas"
           "Kov"
           "Bal"
           "Geg"
           "Bir"
           "Lie"
           "Rgp"
           "Rgs"
           "Spa"
           "Lap"
           "Grd" |]
      Days =
        [| "Pirmadienis"
           "Antradienis"
           "Trečiadienis"
           "Ketvirtadienis"
           "Penktadienis"
           "Šeštadienis"
           "Sekmadienis" |]
      AbbreviatedDays = [| "Pr"; "An"; "Tr"; "Kt"; "Pn"; "Št"; "Sk" |] }

let private lvLv =
    { Months =
        [| "janvāris"
           "februāris"
           "marts"
           "aprīlis"
           "maijs"
           "jūnijs"
           "jūlijs"
           "augusts"
           "septembris"
           "oktobris"
           "novembris"
           "decembris" |]
      AbbreviatedMonths =
        [| "jan"
           "feb"
           "mar"
           "apr"
           "mai"
           "jūn"
           "jūl"
           "aug"
           "sep"
           "okt"
           "nov"
           "dec" |]
      Days =
        [| "pirmdiena"
           "otrdiena"
           "trešdiena"
           "ceturtdiena"
           "piektdiena"
           "sestdiena"
           "svētdiena" |]
      AbbreviatedDays = [| "P "; "O "; "T "; "C "; "Pk"; "S "; "Sv" |] }

let private mkMk =
    { Months =
        [| "јануари"
           "февруари"
           "март"
           "април"
           "мај"
           "јуни"
           "јули"
           "август"
           "септември"
           "октомври"
           "ноември"
           "декември" |]
      AbbreviatedMonths =
        [| "јан"
           "фев"
           "мар"
           "апр"
           "мај"
           "јун"
           "јул"
           "авг"
           "сеп"
           "окт"
           "ное"
           "дек" |]
      Days = [| "понеделник"; "вторник"; "среда"; "четврток"; "петок"; "сабота"; "недела" |]
      AbbreviatedDays = [| "пон"; "вто"; "сре"; "чет"; "пет"; "саб"; "нед" |] }

let private mnMn =
    { Months =
        [| "Нэгдүгээр сар"
           "Хоёрдугаар сар"
           "Гуравдугаар сар"
           "Дөрөвдүгээр сар"
           "Тавдугаар сар"
           "Зургаадугар сар"
           "Долоодугаар сар"
           "Наймдугаар сар"
           "Есдүгээр сар"
           "Аравдугаар сар"
           "Арваннэгдүгээр сар"
           "Арванхоёрдгаар сар" |]
      AbbreviatedMonths =
        [| "1-р"
           "2-р"
           "3-р"
           "4-р"
           "5-р"
           "6-р"
           "7-р"
           "8-р"
           "9-р"
           "10-р"
           "11-р"
           "12-р" |]
      Days = [| "Даваа"; "Мягмар"; "Лхагва"; "Пүрэв"; "Баасан"; "Бямба"; "Ням" |]
      AbbreviatedDays = [| "Да"; "Мя"; "Лх"; "Пү"; "Ба"; "Бя"; "Ня" |] }

let private msMy =
    { Months =
        [| "Januari"
           "Februari"
           "Mac"
           "April"
           "Mei"
           "Jun"
           "Julai"
           "Ogos"
           "September"
           "Oktober"
           "November"
           "Disember" |]
      AbbreviatedMonths =
        [| "Jan"
           "Feb"
           "Mac"
           "Apr"
           "Mei"
           "Jun"
           "Jul"
           "Ogos"
           "Sep"
           "Okt"
           "Nov"
           "Dis" |]
      Days = [| "Isnin"; "Selasa"; "Rabu"; "Khamis"; "Jumaat"; "Sabtu"; "Ahad" |]
      AbbreviatedDays = [| "Isn"; "Sel"; "Rab"; "Kha"; "Jum"; "Sab"; "Ahd" |] }

let private nbNo =
    { Months =
        [| "januar"
           "februar"
           "mars"
           "april"
           "mai"
           "juni"
           "juli"
           "august"
           "september"
           "oktober"
           "november"
           "desember" |]
      AbbreviatedMonths =
        [| "jan"
           "feb"
           "mar"
           "apr"
           "mai"
           "jun"
           "jul"
           "aug"
           "sep"
           "okt"
           "nov"
           "des" |]
      Days = [| "mandag"; "tirsdag"; "onsdag"; "torsdag"; "fredag"; "lørdag"; "søndag" |]
      AbbreviatedDays = [| "man"; "tir"; "ons"; "tor"; "fre"; "lør"; "søn" |] }

let private nlBe =
    { Months =
        [| "januari"
           "februari"
           "maart"
           "april"
           "mei"
           "juni"
           "juli"
           "augustus"
           "september"
           "oktober"
           "november"
           "december" |]
      AbbreviatedMonths =
        [| "jan"
           "feb"
           "mrt"
           "apr"
           "mei"
           "jun"
           "jul"
           "aug"
           "sep"
           "okt"
           "nov"
           "dec" |]
      Days =
        [| "maandag"
           "dinsdag"
           "woensdag"
           "donderdag"
           "vrijdag"
           "zaterdag"
           "zondag" |]
      AbbreviatedDays = [| "ma"; "di"; "wo"; "do"; "vr"; "za"; "zo" |] }

let private plPl =
    { Months =
        [| "styczeń"
           "luty"
           "marzec"
           "kwiecień"
           "maj"
           "czerwiec"
           "lipiec"
           "sierpień"
           "wrzesień"
           "październik"
           "listopad"
           "grudzień" |]
      AbbreviatedMonths =
        [| "sty"
           "lut"
           "mar"
           "kwi"
           "maj"
           "cze"
           "lip"
           "sie"
           "wrz"
           "paź"
           "lis"
           "gru" |]
      Days =
        [| "poniedziałek"
           "wtorek"
           "środa"
           "czwartek"
           "piątek"
           "sobota"
           "niedziela" |]
      AbbreviatedDays = [| "pon"; "wto"; "śro"; "czw"; "pią"; "sob"; "nie" |] }

let private ptBr =
    { Months =
        [| "janeiro"
           "fevereiro"
           "março"
           "abril"
           "maio"
           "junho"
           "julho"
           "agosto"
           "setembro"
           "outubro"
           "novembro"
           "dezembro" |]
      AbbreviatedMonths =
        [| "Jan"
           "Fev"
           "Mar"
           "Abr"
           "Mai"
           "Jun"
           "Jul"
           "Ago"
           "Set"
           "Out"
           "Nov"
           "Dez" |]
      Days = [| "segunda"; "terça"; "quarta"; "quinta"; "sexta"; "sábado"; "domingo" |]
      AbbreviatedDays = [| "Seg"; "Ter"; "Qua"; "Qui"; "Sex"; "Sáb"; "Dom" |] }

let private ptPt =
    { Months =
        [| "Janeiro"
           "Fevereiro"
           "Março"
           "Abril"
           "Maio"
           "Junho"
           "Julho"
           "Agosto"
           "Setembro"
           "Outubro"
           "Novembro"
           "Dezembro" |]
      AbbreviatedMonths =
        [| "Jan"
           "Fev"
           "Mar"
           "Abr"
           "Mai"
           "Jun"
           "Jul"
           "Ago"
           "Set"
           "Out"
           "Nov"
           "Dez" |]
      Days = [| "Segunda"; "Terça"; "Quarta"; "Quinta"; "Sexta"; "Sábado"; "Domingo" |]
      AbbreviatedDays = [| "Seg"; "Ter"; "Qua"; "Qui"; "Sex"; "Sáb"; "Dom" |] }

let private rmCh =
    { Months =
        [| "schaner"
           "favrer"
           "mars"
           "avrigl"
           "matg"
           "zercladur"
           "fanadur"
           "avust"
           "settember"
           "october"
           "november"
           "december" |]
      AbbreviatedMonths =
        [| "schan"
           "favr"
           "mars"
           "avr"
           "matg"
           "zercl"
           "fan"
           "avust"
           "sett"
           "oct"
           "nov"
           "dec" |]
      Days =
        [| "glindesdi"
           "mardi"
           "mesemna"
           "gievgia"
           "venderdi"
           "sonda"
           "dumengia" |]
      AbbreviatedDays = [| "gli"; "ma"; "me"; "gie"; "ve"; "so"; "du" |] }

let private roRo =
    { Months =
        [| "Ianuarie"
           "Februarie"
           "Martie"
           "Aprilie"
           "Mai"
           "Iunie"
           "Iulie"
           "August"
           "Septembrie"
           "Octombrie"
           "Noiembrie"
           "Decembrie" |]
      AbbreviatedMonths =
        [| "ian"
           "feb"
           "mar"
           "apr"
           "mai"
           "iun"
           "iul"
           "aug"
           "sep"
           "oct"
           "nov"
           "dec" |]
      Days = [| "Luni"; "Marţi"; "Miercuri"; "Joi"; "Vineri"; "Sâmbătă"; "Duminică" |]
      AbbreviatedDays = [| "Lu"; "Ma"; "Mi"; "Jo"; "Vi"; "Sâ"; "Du" |] }

let private ruRu =
    { Months =
        [| "Января"
           "Февраля"
           "Марта"
           "Апреля"
           "Мая"
           "Июня"
           "Июля"
           "Августа"
           "Сентября"
           "Октября"
           "Ноября"
           "Декабря" |]
      AbbreviatedMonths =
        [| "Янв"
           "Фев"
           "Мар"
           "Апр"
           "Май"
           "Июн"
           "Июл"
           "Авг"
           "Сен"
           "Окт"
           "Ноя"
           "Дек" |]
      Days =
        [| "Понедельник"
           "Вторник"
           "Среда"
           "Четверг"
           "Пятница"
           "Суббота"
           "Воскресенье" |]
      AbbreviatedDays = [| "Пнд"; "Втр"; "Срд"; "Чтв"; "Птн"; "Сбт"; "Вск" |] }

let private ruUa =
    { Months =
        [| "Январь"
           "Февраль"
           "Март"
           "Апрель"
           "Май"
           "Июнь"
           "Июль"
           "Август"
           "Сентябрь"
           "Октябрь"
           "Ноябрь"
           "Декабрь" |]
      AbbreviatedMonths =
        [| "Янв"
           "Фев"
           "Мар"
           "Апр"
           "Май"
           "Июн"
           "Июл"
           "Авг"
           "Сен"
           "Окт"
           "Ноя"
           "Дек" |]
      Days =
        [| "Понедельник"
           "Вторник"
           "Среда"
           "Четверг"
           "Пятница"
           "Суббота"
           "Воскресенье" |]
      AbbreviatedDays = [| "Пнд"; "Вто"; "Срд"; "Чтв"; "Птн"; "Суб"; "Вск" |] }

let private skSk =
    { Months =
        [| "január"
           "február"
           "marec"
           "apríl"
           "máj"
           "jún"
           "júl"
           "august"
           "september"
           "október"
           "november"
           "december" |]
      AbbreviatedMonths =
        [| "jan"
           "feb"
           "mar"
           "apr"
           "máj"
           "jún"
           "júl"
           "aug"
           "sep"
           "okt"
           "nov"
           "dec" |]
      Days = [| "Pondelok"; "Utorok"; "Streda"; "Štvrtok"; "Piatok"; "Sobota"; "Nedeľa" |]
      AbbreviatedDays = [| "Po"; "Ut"; "St"; "Št"; "Pi"; "So"; "Ne" |] }

let private slSi =
    { Months =
        [| "januar"
           "februar"
           "marec"
           "april"
           "maj"
           "junij"
           "julij"
           "avgust"
           "september"
           "oktober"
           "november"
           "december" |]
      AbbreviatedMonths =
        [| "jan"
           "feb"
           "mar"
           "apr"
           "maj"
           "jun"
           "jul"
           "avg"
           "sep"
           "okt"
           "nov"
           "dec" |]
      Days = [| "ponedeljek"; "torek"; "sreda"; "četrtek"; "petek"; "sobota"; "nedelja" |]
      AbbreviatedDays = [| "pon"; "tor"; "sre"; "čet"; "pet"; "sob"; "ned" |] }

let private sqAl =
    { Months =
        [| "janar"
           "shkurt"
           "mars"
           "prill"
           "maj"
           "qershor"
           "korrik"
           "gusht"
           "shtator"
           "tetor"
           "nëntor"
           "dhjetor" |]
      AbbreviatedMonths =
        [| "Jan"
           "Shk"
           "Mar"
           "Pri"
           "Maj"
           "Qer"
           "Kor"
           "Gsh"
           "Sht"
           "Tet"
           "Nën"
           "Dhj" |]
      Days =
        [| "e hënë "
           "e martë "
           "e mërkurë "
           "e enjte "
           "e premte "
           "e shtunë "
           "e diel " |]
      AbbreviatedDays = [| "Hën "; "Mar "; "Mër "; "Enj "; "Pre "; "Sht "; "Die " |] }

let private srRs =
    { Months =
        [| "januar"
           "februar"
           "mart"
           "april"
           "maj"
           "juni"
           "juli"
           "avgust"
           "septembar"
           "oktobar"
           "novembar"
           "decembar" |]
      AbbreviatedMonths =
        [| "jan"
           "feb"
           "mar"
           "apr"
           "maj"
           "jun"
           "jul"
           "avg"
           "sep"
           "okt"
           "nov"
           "dec" |]
      Days = [| "ponedeljak"; "utorak"; "sreda"; "četvrtak"; "petak"; "subota"; "nedelja" |]
      AbbreviatedDays = [| "pon"; "uto"; "sre"; "čet"; "pet"; "sub"; "ned" |] }

let private svFi =
    { Months =
        [| "januari"
           "februari"
           "mars"
           "april"
           "maj"
           "juni"
           "juli"
           "augusti"
           "september"
           "oktober"
           "november"
           "december" |]
      AbbreviatedMonths =
        [| "jan"
           "feb"
           "mar"
           "apr"
           "maj"
           "jun"
           "jul"
           "aug"
           "sep"
           "okt"
           "nov"
           "dec" |]
      Days = [| "måndag"; "tisdag"; "onsdag"; "torsdag"; "fredag"; "lördag"; "söndag" |]
      AbbreviatedDays = [| "mån"; "tis"; "ons"; "tor"; "fre"; "lör"; "sön" |] }

let private taIn =
    { Months =
        [| "ஜனவரி"
           "பெப்ரவரி"
           "மார்ச்"
           "ஏப்ரல்"
           "மே"
           "ஜூன்"
           "ஜூலை"
           "ஆகஸ்ட்"
           "செப்டம்பர்"
           "அக்டோபர்"
           "நவம்பர்"
           "டிசம்பர்r" |]
      AbbreviatedMonths =
        [| "ஜனவரி"
           "பெப்ரவரி"
           "மார்ச்"
           "ஏப்ரல்"
           "மே"
           "ஜூன்"
           "ஜூலை"
           "ஆகஸ்ட்"
           "செப்டம்பர்"
           "அக்டோபர்"
           "நவம்பர்"
           "டிசம்பர்r" |]
      Days = [| "திங்கள்"; "செவ்வாய்"; "புதன்"; "வியாழன்"; "வெள்ளி"; "சனி"; "ஞாயிறு" |]
      AbbreviatedDays = [| "த"; "ச"; "ப"; "வ"; "வ"; "ச"; "ஞ" |] }

let private teIn =
    { Months =
        [| "జనవరి"
           "ఫిబ్రవరి"
           "మార్చి"
           "ఏప్రిల్"
           "మే"
           "జూన్"
           "జూలై"
           "ఆగస్టు"
           "సెప్టెంబర్"
           "అక్టోబర్"
           "నవంబర్"
           "డిసెంబర్" |]
      AbbreviatedMonths =
        [| "జనవరి"
           "ఫిబ్రవరి"
           "మార్చి"
           "ఏప్రిల్"
           "మే"
           "జూన్"
           "జూలై"
           "ఆగస్టు"
           "సెప్టెంబర్"
           "అక్టోబర్"
           "నవంబర్"
           "డిసెంబర్" |]
      Days =
        [| "సోమవారం"
           "మంగళవారం"
           "బుధవారం"
           "గురువారం"
           "శుక్రవారం"
           "శనివారం"
           "ఆదివారం" |]
      AbbreviatedDays = [| "సోమ"; "మంగళ"; "బుధ"; "గురు"; "శుక్ర"; "శని"; "ఆది" |] }

let private thTh =
    { Months =
        [| "มกราคม"
           "กุมภาพันธ์"
           "มีนาคม"
           "เมษายน"
           "พฤษภาคม"
           "มิถุนายน"
           "กรกฎาคม"
           "สิงหาคม"
           "กันยายน"
           "ตุลาคม"
           "พฤศจิกายน"
           "ธันวาคม" |]
      AbbreviatedMonths =
        [| "ม.ค."
           "ก.พ."
           "มี.ค."
           "เม.ย."
           "พ.ค."
           "มิ.ย."
           "ก.ค."
           "ส.ค."
           "ก.ย."
           "ต.ค."
           "พ.ย."
           "ธ.ค." |]
      Days = [| "จันทร์"; "อังคาร"; "พุธ"; "พฤหัสบดี"; "ศุกร์"; "เสาร์"; "อาทิตย์" |]
      AbbreviatedDays = [| "จ."; "อ."; "พ."; "พฤ."; "ศ."; "ส."; "อา." |] }

let private trTr =
    { Months =
        [| "Ocak"
           "Şubat"
           "Mart"
           "Nisan"
           "Mayıs"
           "Haziran"
           "Temmuz"
           "Ağustos"
           "Eylül"
           "Ekim"
           "Kasım"
           "Aralık" |]
      AbbreviatedMonths =
        [| "Oca"
           "Şub"
           "Mar"
           "Nis"
           "May"
           "Haz"
           "Tem"
           "Ağu"
           "Eyl"
           "Eki"
           "Kas"
           "Ara" |]
      Days = [| "Pazartesi"; "Salı"; "Çarşamba"; "Perşembe"; "Cuma"; "Cumartesi"; "Pazar" |]
      AbbreviatedDays = [| "Pzt"; "Sal"; "Çrş"; "Prş"; "Cum"; "Cts"; "Paz" |] }

let private ukUa =
    { Months =
        [| "Січень"
           "Лютий"
           "Березень"
           "Квітень"
           "Травень"
           "Червень"
           "Липень"
           "Серпень"
           "Вересень"
           "Жовтень"
           "Листопад"
           "Грудень" |]
      AbbreviatedMonths =
        [| "Січ"
           "Лют"
           "Бер"
           "Кві"
           "Тра"
           "Чер"
           "Лип"
           "Сер"
           "Вер"
           "Жов"
           "Лис"
           "Гру" |]
      Days =
        [| "Понеділок"
           "Вівторок"
           "Середа"
           "Четвер"
           "П'ятниця"
           "Субота"
           "Неділя" |]
      AbbreviatedDays = [| "Пнд"; "Втр"; "Срд"; "Чтв"; "Птн"; "Сбт"; "Ндл" |] }

let private urPk =
    { Months =
        [| "جنوري"
           "فروري"
           "مارچ"
           "اپريل"
           "مٓی"
           "جون"
           "جولاي"
           "اگست"
           "ستمبر"
           "اكتوبر"
           "نومبر"
           "دسمبر" |]
      AbbreviatedMonths =
        [| "جنوري"
           "فروري"
           "مارچ"
           "اپريل"
           "مٓی"
           "جون"
           "جولاي"
           "اگست"
           "ستمبر"
           "اكتوبر"
           "نومبر"
           "دسمبر" |]
      Days = [| "پير"; "منگل"; "بدھ"; "جمعرات"; "جمعه"; "هفته"; "اتوار" |]
      AbbreviatedDays = [| "پير"; "منگل"; "بدھ"; "جمعرات"; "جمعه"; "هفته"; "اتوار" |] }

let private viVn =
    { Months =
        [| "Tháng một"
           "Tháng hai"
           "Tháng ba"
           "Tháng tư"
           "Tháng năm"
           "Tháng sáu"
           "Tháng bảy"
           "Tháng tám"
           "Tháng chín"
           "Tháng mười"
           "Tháng mười một"
           "Tháng mười hai" |]
      AbbreviatedMonths =
        [| "Thg 1"
           "Thg 2"
           "Thg 3"
           "Thg 4"
           "Thg 5"
           "Thg 6"
           "Thg 7"
           "Thg 8"
           "Thg 9"
           "Thg 10"
           "Thg 11"
           "Thg 12" |]
      Days =
        [| "Thứ hai "
           "Thứ ba "
           "Thứ tư "
           "Thứ năm "
           "Thứ sáu "
           "Thứ bảy "
           "Chủ nhật " |]
      AbbreviatedDays = [| "Th 2 "; "Th 3 "; "Th 4 "; "Th 5 "; "Th 6 "; "Th 7 "; "CN " |] }

let private zhCn =
    { Months = [| "一月"; "二月"; "三月"; "四月"; "五月"; "六月"; "七月"; "八月"; "九月"; "十月"; "十一月"; "十二月" |]
      AbbreviatedMonths =
        [| " 1月"
           " 2月"
           " 3月"
           " 4月"
           " 5月"
           " 6月"
           " 7月"
           " 8月"
           " 9月"
           "10月"
           "11月"
           "12月" |]
      Days = [| "星期一"; "星期二"; "星期三"; "星期四"; "星期五"; "星期六"; "星期日" |]
      AbbreviatedDays = [| "一"; "二"; "三"; "四"; "五"; "六"; "日" |] }

let private zhTw =
    { Months = [| "一月"; "二月"; "三月"; "四月"; "五月"; "六月"; "七月"; "八月"; "九月"; "十月"; "十一月"; "十二月" |]
      AbbreviatedMonths =
        [| " 1月"
           " 2月"
           " 3月"
           " 4月"
           " 5月"
           " 6月"
           " 7月"
           " 8月"
           " 9月"
           "10月"
           "11月"
           "12月" |]
      Days = [| "週一"; "週二"; "週三"; "週四"; "週五"; "週六"; "週日" |]
      AbbreviatedDays = [| "一"; "二"; "三"; "四"; "五"; "六"; "日" |] }

let private byName =
    [ ([ "AR_AE" ], arAe)
      ([ "AR_BH"
         "AR_DZ"
         "AR_EG"
         "AR_IN"
         "AR_IQ"
         "AR_KW"
         "AR_LY"
         "AR_MA"
         "AR_OM"
         "AR_QA"
         "AR_SD"
         "AR_TN"
         "AR_YE" ],
       arBh)
      ([ "AR_JO"; "AR_LB" ], arJo)
      ([ "AR_SA" ], arSa)
      ([ "AR_SY" ], arSy)
      ([ "BE_BY" ], beBy)
      ([ "BG_BG" ], bgBg)
      ([ "CA_ES" ], caEs)
      ([ "CS_CZ" ], csCz)
      ([ "DA_DK" ], daDk)
      ([ "DE_AT" ], deAt)
      ([ "DE_BE"; "DE_CH"; "DE_DE"; "DE_LU" ], deBe)
      ([ "EL_GR" ], elGr)
      ([ "EN_AU"
         "EN_CA"
         "EN_GB"
         "EN_IN"
         "EN_NZ"
         "EN_PH"
         "EN_US"
         "EN_ZA"
         "EN_ZW" ],
       enAu)
      ([ "ES_AR"
         "ES_BO"
         "ES_CL"
         "ES_CO"
         "ES_CR"
         "ES_DO"
         "ES_EC"
         "ES_ES"
         "ES_GT"
         "ES_HN"
         "ES_MX"
         "ES_NI"
         "ES_PA"
         "ES_PE"
         "ES_PR"
         "ES_PY"
         "ES_SV"
         "ES_US"
         "ES_UY"
         "ES_VE" ],
       esAr)
      ([ "ET_EE" ], etEe)
      ([ "EU_ES" ], euEs)
      ([ "FI_FI" ], fiFi)
      ([ "FO_FO" ], foFo)
      ([ "FR_BE"; "FR_CA"; "FR_CH"; "FR_FR"; "FR_LU" ], frBe)
      ([ "GL_ES" ], glEs)
      ([ "GU_IN" ], guIn)
      ([ "HE_IL" ], heIl)
      ([ "HI_IN" ], hiIn)
      ([ "HR_HR" ], hrHr)
      ([ "HU_HU" ], huHu)
      ([ "ID_ID" ], idId)
      ([ "IS_IS" ], isIs)
      ([ "IT_CH"; "IT_IT" ], itCh)
      ([ "JA_JP" ], jaJp)
      ([ "KO_KR" ], koKr)
      ([ "LT_LT" ], ltLt)
      ([ "LV_LV" ], lvLv)
      ([ "MK_MK" ], mkMk)
      ([ "MN_MN" ], mnMn)
      ([ "MS_MY" ], msMy)
      ([ "NB_NO"; "NO_NO" ], nbNo)
      ([ "NL_BE"; "NL_NL" ], nlBe)
      ([ "PL_PL" ], plPl)
      ([ "PT_BR" ], ptBr)
      ([ "PT_PT" ], ptPt)
      ([ "RM_CH" ], rmCh)
      ([ "RO_RO" ], roRo)
      ([ "RU_RU" ], ruRu)
      ([ "RU_UA" ], ruUa)
      ([ "SK_SK" ], skSk)
      ([ "SL_SI" ], slSi)
      ([ "SQ_AL" ], sqAl)
      ([ "SR_RS" ], srRs)
      ([ "SV_FI"; "SV_SE" ], svFi)
      ([ "TA_IN" ], taIn)
      ([ "TE_IN" ], teIn)
      ([ "TH_TH" ], thTh)
      ([ "TR_TR" ], trTr)
      ([ "UK_UA" ], ukUa)
      ([ "UR_PK" ], urPk)
      ([ "VI_VN" ], viVn)
      ([ "ZH_CN"; "ZH_HK" ], zhCn)
      ([ "ZH_TW" ], zhTw) ]
    |> Seq.collect (fun (locales, names) -> locales |> Seq.map (fun locale -> locale, names))
    |> Map.ofSeq

let tryFind (locale: string) =
    Map.tryFind (locale.ToUpperInvariant()) byName

let names = byName |> Map.toSeq |> Seq.map fst
