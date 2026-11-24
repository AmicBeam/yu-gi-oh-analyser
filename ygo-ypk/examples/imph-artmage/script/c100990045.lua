-- 无垢奥艺之绊 神艺（仅实现②）
local s,id,o=GetID()
function s.initial_effect(c)
 c:EnableReviveLimit()
 aux.AddLinkProcedure(c,nil,2,4,s.lcheck)
 local e2=Effect.CreateEffect(c)
 e2:SetDescription(aux.Stringid(id,0))
 e2:SetCategory(CATEGORY_TOHAND+CATEGORY_SEARCH+CATEGORY_SPECIAL_SUMMON)
 e2:SetType(EFFECT_TYPE_IGNITION)
 e2:SetRange(LOCATION_MZONE)
 e2:SetCountLimit(1,id)
 e2:SetCondition(s.ignitioncondition)
 e2:SetTarget(s.target)
 e2:SetOperation(s.operation)
 c:RegisterEffect(e2)
 -- 黄昏之变幻 converts the same effect to a Quick Effect.
 -- Both versions retain the same card-name count code.
 local e3=e2:Clone()
 e3:SetType(EFFECT_TYPE_QUICK_O)
 e3:SetCode(EVENT_FREE_CHAIN)
 e3:SetHintTiming(0,TIMINGS_CHECK_MONSTER+TIMING_END_PHASE)
 e3:SetCondition(s.quickcondition)
 c:RegisterEffect(e3)
end
function s.quickcondition(e,tp)
 return s.isquick(e:GetHandler(),tp)
end
function s.isquick(c,tp)
 -- Equivalent to aux.IsCanBeQuickEffect(...,37279096), without
 -- requiring that helper in older clients' utility.lua.
 return Duel.IsPlayerAffectedByEffect(tp,37279096)~=nil
  and c:IsOriginalSetCard(0x1e6)
end
function s.ignitioncondition(e,tp)
 return not s.quickcondition(e,tp)
end
function s.matfilter(c)
 return c:IsLinkSetCard(0x1e6) or c:IsLinkSetCard(0x1cd)
end
function s.lcheck(g)
 return g:IsExists(s.matfilter,1,nil)
end
function s.deckfilter(c,e,tp)
 return c:IsType(TYPE_MONSTER)
  and (c:IsSetCard(0x1e6) or c:IsSetCard(0x1cd) or c:IsSetCard(0x1d2) or c:IsSetCard(0x1d8))
  and (c:IsAbleToHand() or (Duel.GetLocationCount(tp,LOCATION_MZONE)>0
   and c:IsCanBeSpecialSummoned(e,0,tp,false,false)))
end
function s.target(e,tp,eg,ep,ev,re,r,rp,chk)
 if chk==0 then return Duel.IsExistingMatchingCard(s.deckfilter,tp,LOCATION_DECK,0,1,nil,e,tp) end
 Duel.SetOperationInfo(0,CATEGORY_TOHAND,nil,1,tp,LOCATION_DECK)
 Duel.SetOperationInfo(0,CATEGORY_SPECIAL_SUMMON,nil,1,tp,LOCATION_DECK+LOCATION_GRAVE+LOCATION_REMOVED)
end
function s.revivefilter(c,e,tp)
 return c:IsType(TYPE_MONSTER) and (not c:IsLocation(LOCATION_REMOVED) or c:IsFaceup())
  and c:IsCanBeSpecialSummoned(e,0,tp,false,false)
end
function s.operation(e,tp,eg,ep,ev,re,r,rp)
 Duel.Hint(HINT_SELECTMSG,tp,HINTMSG_SELECT)
 local g=Duel.SelectMatchingCard(tp,s.deckfilter,tp,LOCATION_DECK,0,1,1,nil,e,tp)
 local tc=g:GetFirst()
 if not tc then return end
 local th=tc:IsAbleToHand()
 local sp=Duel.GetLocationCount(tp,LOCATION_MZONE)>0 and tc:IsCanBeSpecialSummoned(e,0,tp,false,false)
 local opt=0
 if th and sp then
  opt=Duel.SelectOption(tp,aux.Stringid(id,1),aux.Stringid(id,2))
 elseif sp then opt=1
 elseif not th then return end
 local success=false
 if opt==0 then
  success=Duel.SendtoHand(tc,nil,REASON_EFFECT)>0
  if success then Duel.ConfirmCards(1-tp,tc) end
 else
  success=Duel.SpecialSummon(tc,0,tp,tp,false,false,POS_FACEUP)>0
 end
 if not success then return end
 local fg=Duel.GetMatchingGroup(Card.IsFaceup,tp,LOCATION_MZONE,0,nil)
 if fg:GetClassCount(Card.GetRace)<3 or Duel.GetLocationCount(tp,LOCATION_MZONE)<=0 then return end
 local rf=aux.NecroValleyFilter(s.revivefilter)
 if not Duel.IsExistingMatchingCard(rf,tp,LOCATION_GRAVE+LOCATION_REMOVED,0,1,nil,e,tp)
  or not Duel.SelectYesNo(tp,aux.Stringid(id,3)) then return end
 Duel.BreakEffect()
 Duel.Hint(HINT_SELECTMSG,tp,HINTMSG_SPSUMMON)
 local rg=Duel.SelectMatchingCard(tp,rf,tp,LOCATION_GRAVE+LOCATION_REMOVED,0,1,1,nil,e,tp)
 if rg:GetCount()>0 then Duel.SpecialSummon(rg,0,tp,tp,false,false,POS_FACEUP) end
end
