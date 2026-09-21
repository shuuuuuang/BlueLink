package com.bluelink.android

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Parcel
import android.provider.OpenableColumns
import com.bluelink.android.domain.*
import com.bluelink.android.files.FileInteraction
import com.bluelink.android.sharing.ShareInbox
import java.io.File
import java.util.UUID

/** Exercises actual Intent parceling, exported file bytes and the real receiving parser. */
internal suspend fun verifySeparateMessageSharing(context: Context, fixtures: List<ChatItem>) {
    var checks=0
    fun verify(ok: Boolean) { check(ok); checks++ }
    fun roundTrip(intent: Intent): Intent {
        val p=Parcel.obtain()
        return try { intent.writeToParcel(p,0);p.setDataPosition(0);Intent.CREATOR.createFromParcel(p) } finally {p.recycle()}
    }
    val a=fixtures[0].copy(text="  文本1 🌍\n内部换行  ")
    val b=fixtures[0].copy(id=UUID(0,11),text="文本2")
    val c=fixtures[3].copy(text="文本3")
    val d=fixtures[3].copy(id=UUID(0,12),text="文本4")
    val separator=MessageShareContent.SEPARATOR
    val plain=roundTrip(FileInteraction.messageShareIntent(context,listOf(a,b)))
    verify(plain.action==Intent.ACTION_SEND && plain.type=="text/plain")
    verify(plain.getStringExtra(Intent.EXTRA_TEXT)==a.text+separator+b.text)
    verify(plain.clipData!!.itemCount==1 && plain.clipData!!.getItemAt(0).text.toString()==a.text+separator+b.text)
    verify(!plain.hasExtra(Intent.EXTRA_STREAM))
    val mixed=roundTrip(FileInteraction.messageShareIntent(context,listOf(a,b,fixtures[1],c,d)))
    verify(mixed.action==Intent.ACTION_SEND_MULTIPLE && mixed.type=="*/*" && !mixed.hasExtra(Intent.EXTRA_TEXT))
    val uris=mixed.getParcelableArrayListExtra(Intent.EXTRA_STREAM,Uri::class.java)!!
    verify(uris.size==3 && mixed.clipData!!.itemCount==3)
    verify(mixed.flags and Intent.FLAG_GRANT_READ_URI_PERMISSION!=0)
    val names=uris.map {uri->context.contentResolver.query(uri,arrayOf(OpenableColumns.DISPLAY_NAME),null,null,null)!!.use {it.moveToFirst();it.getString(0)}}
    verify(names==listOf(MessageShareContent.textFileName(a.text,1,context.getString(R.string.message_share_text_file)),fixtures[1].attachments.single().fileName,MessageShareContent.textFileName(c.text,2,context.getString(R.string.message_share_text_file))))
    uris.forEachIndexed {i,uri->verify(mixed.clipData!!.getItemAt(i).uri==uri)}
    val contents=uris.map {uri->context.contentResolver.openInputStream(uri)!!.bufferedReader(Charsets.UTF_8).use {it.readText()}}
    verify(contents==listOf(a.text+separator+b.text,"QA preserved file bytes",c.text+separator+d.text))
    val chooser=roundTrip(Intent.createChooser(mixed,null))
    verify(chooser.flags and Intent.FLAG_GRANT_READ_URI_PERMISSION!=0)
    val inbox=ShareInbox(context,File(context.cacheDir,"share-groups-qa-${UUID.randomUUID()}"))
    val textId=inbox.capture(plain)
    verify(inbox.requests.value.single {it.id==textId}.text==a.text+separator+b.text)
    inbox.discard(textId)
    val fileId=inbox.capture(chooser.getParcelableExtra(Intent.EXTRA_INTENT,Intent::class.java)!!)
    val received=inbox.requests.value.single {it.id==fileId}
    verify(received.text.isEmpty() && received.files.map {it.name}==names)
    verify(received.files.map { inbox.source(fileId,it).readText(Charsets.UTF_8) }==contents)
    inbox.discard(fileId)
    val originals=FileInteraction.messageShareIntent(context,listOf(fixtures[1],fixtures[2]))
    verify(originals.action==Intent.ACTION_SEND_MULTIPLE && originals.clipData!!.itemCount==2)
    val single=FileInteraction.messageShareIntent(context,listOf(a))
    verify(single.getStringExtra(Intent.EXTRA_TEXT)==a.text)
    val colliding=fixtures[1].copy(attachments=fixtures[1].attachments.map {it.copy(fileName=MessageShareContent.textFileName(a.text,1,context.getString(R.string.message_share_text_file)))})
    val collision=FileInteraction.messageShareIntent(context,listOf(a,colliding))
    val firstUri=collision.clipData!!.getItemAt(0).uri
    val unique=context.contentResolver.query(firstUri,arrayOf(OpenableColumns.DISPLAY_NAME),null,null,null)!!.use {it.moveToFirst();it.getString(0)}
    verify(unique!=colliding.attachments.single().fileName && unique.endsWith(".txt"))
    val invalid=fixtures[1].copy(attachments=fixtures[1].attachments.map {it.copy(state="RECEIVING")})
    verify(runCatching {FileInteraction.messageShareIntent(context,listOf(a,invalid))}.isFailure)
    File(context.cacheDir,"message-share-groups-result.txt").writeText("PASS $checks checks: merged text separator, exactly 3 ordered files, UTF-8 bytes, exported names, URI grants, actual receiver parser, collision handling and invalid-file rejection")
}
