-- 神幻学都 奥艺（仅实现②；规则字段由CDB提供）
local s,id,o=GetID()
function s.initial_effect(c)
 local ea=Effect.CreateEffect(c)
 ea:SetType(EFFECT_TYPE_ACTIVATE)
 ea:SetCode(EVENT_FREE_CHAIN)
 c:RegisterEffect(ea)
 local e2=Effect.CreateEffect(c)
 e2:SetDescription(aux.Stringid(id,0))
 e2:SetCategory(CATEGORY_SPECIAL_SUMMON)
 e2:SetType(EFFECT_TYPE_IGNITION)
 e2:SetRange(LOCATION_FZONE)
 e2:SetCondition(s.condition)
 e2:SetCost(s.cost)
 e2:SetTarget(s.target)
 e2:SetOperation(s.operation)
 c:RegisterEffect(e2)
end
function s.godfilter(c)
 return c:IsType(TYPE_MONSTER) and c:IsSetCard(0x1ce)
end
function s.condition(e,tp)
 return Duel.IsExistingMatchingCard(s.godfilter,tp,LOCATION_GRAVE,0,1,nil)
end
function s.costfilter(c)
 return c:IsType(TYPE_FUSION+TYPE_SYNCHRO+TYPE_XYZ) and c:IsAbleToRemoveAsCost()
end
function s.usedname(tp,code)
 for _,v in ipairs({Duel.GetFlagEffectLabel(tp,id)}) do
  if v==code then return true end
 end
 return false
end
function s.exfilter(c,e,tp,code)
 return c:IsType(TYPE_LINK) and c:IsSetCard(0x1e6)
  and (not code or c:IsCode(code)) and not s.usedname(tp,c:GetOriginalCode())
  and c:IsCanBeSpecialSummoned(e,0,tp,false,false)
  and Duel.GetLocationCountFromEx(tp,tp,nil,c)>0
end
function s.cost(e,tp,eg,ep,ev,re,r,rp,chk)
 if chk==0 then return Duel.IsExistingMatchingCard(s.costfilter,tp,LOCATION_GRAVE,0,1,nil) end
 Duel.Hint(HINT_SELECTMSG,tp,HINTMSG_REMOVE)
 local g=Duel.SelectMatchingCard(tp,s.costfilter,tp,LOCATION_GRAVE,0,1,1,nil)
 Duel.Remove(g,POS_FACEUP,REASON_COST)
end
function s.target(e,tp,eg,ep,ev,re,r,rp,chk)
 if chk==0 then return Duel.IsExistingMatchingCard(s.exfilter,tp,LOCATION_EXTRA,0,1,nil,e,tp) end
 local af={TYPE_LINK,OPCODE_ISTYPE,0x1e6,OPCODE_ISSETCARD,OPCODE_AND}
 for _,code in ipairs({Duel.GetFlagEffectLabel(tp,id)}) do
  table.insert(af,code)
  table.insert(af,OPCODE_ISCODE)
  table.insert(af,OPCODE_NOT)
  table.insert(af,OPCODE_AND)
 end
 getmetatable(e:GetHandler()).announce_filter=af
 local code=Duel.AnnounceCard(tp,table.unpack(af))
 e:SetLabel(code)
 Duel.RegisterFlagEffect(tp,id,RESET_PHASE+PHASE_END,0,1,code)
 Duel.SetOperationInfo(0,CATEGORY_SPECIAL_SUMMON,nil,1,tp,LOCATION_EXTRA)
end
function s.summonfilter(c,e,tp,code)
 return c:IsCode(code) and c:IsCanBeSpecialSummoned(e,0,tp,false,false)
  and Duel.GetLocationCountFromEx(tp,tp,nil,c)>0
end
function s.operation(e,tp,eg,ep,ev,re,r,rp)
 local c=e:GetHandler()
 if not c:IsRelateToEffect(e) then return end
 Duel.Hint(HINT_SELECTMSG,tp,HINTMSG_SPSUMMON)
 local g=Duel.SelectMatchingCard(tp,s.summonfilter,tp,LOCATION_EXTRA,0,1,1,nil,e,tp,e:GetLabel())
 local tc=g:GetFirst()
 if tc and Duel.SpecialSummon(tc,0,tp,tp,false,false,POS_FACEUP)>0 then
  local e1=Effect.CreateEffect(c)
  e1:SetDescription(aux.Stringid(id,1))
  e1:SetType(EFFECT_TYPE_SINGLE)
  e1:SetProperty(EFFECT_FLAG_UNCOPYABLE+EFFECT_FLAG_CLIENT_HINT)
  e1:SetCode(EFFECT_CANNOT_BE_LINK_MATERIAL)
  e1:SetValue(1)
  e1:SetReset(RESET_EVENT+RESETS_STANDARD)
  tc:RegisterEffect(e1)
 end
end
